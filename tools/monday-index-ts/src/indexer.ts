import { existsSync, readFileSync } from "node:fs";
import path from "node:path";
import ts from "typescript";
import { packageDirs, rel, sourceFiles, tsconfigs } from "./files.ts";
import { type CodeEdge, type CodeNode, type EdgeKind, type GraphFile, type NodeKind, hashCode, sortGraph } from "./graph.ts";
import { HTTP_VERBS, type NextFileKind, isMiddlewareFile, nextFile } from "./next-routes.ts";
import { codeFindings, endpointFindings, guardOf } from "./security.ts";

const APP_ID = "app";

const DEFAULT_OPTIONS: ts.CompilerOptions = {
  allowJs: true,
  jsx: ts.JsxEmit.Preserve,
  target: ts.ScriptTarget.ESNext,
  module: ts.ModuleKind.ESNext,
  moduleResolution: ts.ModuleResolutionKind.Bundler,
  skipLibCheck: true,
  noEmit: true,
};

const TEST_FILE = /(\.(test|spec)\.[cm]?[jt]sx?$)|(^|\/)(__tests__|e2e)\//;
const TEST_CALLS = new Set(["test", "it"]);
const SUITE_CALLS = new Set(["describe", "suite"]);

/** A package.json folder: in a monorepo each one is a module; in a single app, its top folders are. */
interface Package {
  dir: string;
  name: string;
}

/** One source file with the program that type-checks it. */
interface Unit {
  file: string;
  sf: ts.SourceFile;
  checker: ts.TypeChecker;
  pkg: Package;
  /** Path inside its package, e.g. "src/app/api/x/route.ts". */
  inner: string;
  fileId: string;
  isTest: boolean;
}

/** What a request reaches: the endpoint and the function that handles it. */
interface EndpointSpec {
  route: string;
  unit: Unit;
  handler: ts.Node | undefined;
  at: ts.Node;
  kind: "route" | "page" | "action";
}

/**
 * Builds Monday's code graph of a TypeScript or JavaScript repo with the TypeScript compiler:
 * modules and features from folders, one node per file, its functions, classes and object services,
 * Next.js route handlers, pages and server actions as endpoints, middleware as the request pipeline,
 * tests, and calls resolved by the type checker across files.
 */
export class RepoIndexer {
  private readonly nodes = new Map<string, CodeNode>();
  private readonly edges: CodeEdge[] = [];
  /** Declaration (file + position) -> node id, so a call resolved in any program lands on the same node. */
  private readonly declared = new Map<string, string>();
  /** The syntax node whose subtree belongs to a graph node: calls inside count as that node's calls. */
  private readonly owners = new Map<ts.Node, string>();
  /** Names worth resolving: an identifier with any other name can't land on a graph node. */
  private readonly names = new Set<string>();
  private readonly endpoints: EndpointSpec[] = [];
  private readonly pages = new Set<string>();
  private readonly bodies = new Map<string, ts.Node>();
  private readonly root: string;
  private readonly appName: string;

  constructor(root: string, appName: string) {
    this.root = path.resolve(root);
    this.appName = appName;
  }

  index(commit: string): GraphFile {
    this.add({ id: APP_ID, kind: "App", name: this.appName, guards: [] });

    const files = sourceFiles(this.root);
    const packages = packageDirs(this.root, files).map((dir) => ({ dir, name: packageName(this.root, dir) }));
    const units = this.units(files, packages);

    // Pass 1: nodes. Pass 2: edges, which need every node to exist first.
    for (const unit of units) this.collectNodes(unit);
    for (const spec of this.endpoints) this.addEndpoint(spec);
    for (const unit of units) this.collectEdges(unit);
    this.linkPipeline(units);

    const middlewareLimits = units.some((u) => isMiddlewareFile(u.inner) && /rate.?limit/i.test(u.sf.text));
    const findings = endpointFindings([...this.nodes.values()].filter((n) => !this.pages.has(n.id)), middlewareLimits);
    for (const unit of units) findings.push(...codeFindings(unit.sf, unit.file, (n) => this.ownerOf(n, unit), unit.isTest));

    return sortGraph({ commit, nodes: [...this.nodes.values()], edges: this.edges, findings });
  }

  // ---------- programs and files ----------

  /** One program per tsconfig.json (so path aliases resolve), and one more for files no tsconfig covers. */
  private units(files: string[], packages: Package[]): Unit[] {
    const wanted = new Map(files.map((f) => [path.resolve(this.root, f), f]));
    const programOf = new Map<string, ts.Program>();
    const host: ts.ParseConfigFileHost = { ...ts.sys, onUnRecoverableConfigFileDiagnostic: () => {} };

    const build = (rootNames: string[], options: ts.CompilerOptions, references?: readonly ts.ProjectReference[]): void => {
      if (rootNames.length === 0) return;
      const program = ts.createProgram({
        rootNames,
        options: { ...options, noEmit: true, incremental: false, composite: false, tsBuildInfoFile: undefined, skipLibCheck: true },
        projectReferences: references,
      });
      for (const f of rootNames) programOf.set(f, program);
    };

    for (const config of tsconfigs(this.root)) {
      const parsed = ts.getParsedCommandLineOfConfigFile(config, {}, host);
      if (!parsed) continue;
      build(
        parsed.fileNames.map((f) => path.resolve(f)).filter((f) => wanted.has(f) && !programOf.has(f)),
        parsed.options,
        parsed.projectReferences,
      );
    }

    build([...wanted.keys()].filter((f) => !programOf.has(f)), DEFAULT_OPTIONS);

    const units: Unit[] = [];
    for (const [abs, file] of wanted) {
      const program = programOf.get(abs);
      const sf = program?.getSourceFile(abs);
      if (!program || !sf) continue;
      const pkg = packages.find((p) => p.dir === "" || file.startsWith(`${p.dir}/`)) ?? { dir: "", name: this.appName };
      const inner = pkg.dir === "" ? file : file.slice(pkg.dir.length + 1);
      units.push({ file, sf, checker: program.getTypeChecker(), pkg, inner, fileId: `file:${file}`, isTest: TEST_FILE.test(file) });
    }

    return units.sort((a, b) => (a.file < b.file ? -1 : 1));
  }

  /**
   * Module and feature for a file. A package other than the repo root is a module and its top folders
   * are features; in the root package (a single app), top folders are modules ("app", "server") and the
   * folders inside them are features ("server/services"). A leading "src/" is skipped.
   */
  private parentFor(unit: Unit): string {
    const parts = unit.inner.split("/");
    const srcOffset = parts[0] === "src" && parts.length > 1 ? 1 : 0;
    const inside = parts.slice(srcOffset);
    const base = unit.pkg.dir === "" ? "" : `${unit.pkg.dir}/`;
    const srcPrefix = srcOffset ? "src/" : "";

    let moduleName: string;
    let moduleDir: string;
    let featureParts: string[];
    if (unit.pkg.dir !== "") {
      moduleName = unit.pkg.name;
      moduleDir = unit.pkg.dir;
      featureParts = inside;
    } else if (inside.length >= 2) {
      moduleName = inside[0];
      moduleDir = `${srcPrefix}${inside[0]}`;
      featureParts = inside.slice(1);
    } else {
      moduleName = srcOffset ? "src" : "root";
      moduleDir = srcOffset ? "src" : ".";
      featureParts = [];
    }

    const moduleId = `module:${moduleName}`;
    this.add({ id: moduleId, kind: "Module", name: moduleName, parentId: APP_ID, file: moduleDir, guards: [] });
    if (featureParts.length < 2) return moduleId;

    const featureId = `feature:${moduleName}/${featureParts[0]}`;
    const featureDir = unit.pkg.dir !== "" ? `${base}${srcPrefix}${featureParts[0]}` : `${moduleDir}/${featureParts[0]}`;
    this.add({ id: featureId, kind: "Feature", name: featureParts[0], parentId: moduleId, file: featureDir, guards: [] });
    return featureId;
  }

  // ---------- pass 1: nodes ----------

  private collectNodes(unit: Unit): void {
    const { sf, file } = unit;
    this.add({ id: unit.fileId, kind: "Type", name: path.posix.basename(file), parentId: this.parentFor(unit), file, startLine: 1, endLine: sf.getLineAndCharacterOfPosition(sf.end).line + 1, guards: [] });
    this.owners.set(sf, unit.fileId);
    const route = nextFile(unit.inner);
    const isServerActions = hasDirective(sf, "use server");

    for (const statement of sf.statements) {
      if (ts.isFunctionDeclaration(statement) && statement.body) {
        const name = statement.name?.text ?? "default";
        this.method(unit, `${file}#${name}`, name, statement, statement, unit.fileId);
        if (isServerActions && isExported(statement) && statement.name) this.action(unit, name, statement);
      } else if (ts.isClassDeclaration(statement)) {
        this.classNode(unit, statement);
      } else if (ts.isVariableStatement(statement)) {
        for (const decl of statement.declarationList.declarations) {
          if (!ts.isIdentifier(decl.name) || !decl.initializer) continue;
          const name = decl.name.text;
          const init = unwrap(decl.initializer);
          const located = statement.declarationList.declarations.length === 1 ? statement : decl;
          if (isFunctionValue(init)) {
            this.method(unit, `${file}#${name}`, name, decl, located, unit.fileId);
            if (isServerActions && isExported(statement)) this.action(unit, name, decl);
          } else if (ts.isObjectLiteralExpression(init) && init.properties.some((p) => memberFunction(p) !== undefined)) {
            this.objectNode(unit, name, decl, init, located);
          } else if (ts.isClassExpression(init)) {
            this.classNode(unit, init, name);
          }
        }
      } else if (ts.isExportAssignment(statement) && isFunctionValue(unwrap(statement.expression))) {
        this.method(unit, `${file}#default`, "default", statement, statement, unit.fileId);
      }
    }

    if (unit.isTest) this.collectTests(unit);
    if (route) this.collectEndpoints(unit, route.kind, route.path);
  }

  private method(unit: Unit, id: string, name: string, owner: ts.Node, located: ts.Node, parentId: string, kind: NodeKind = "Method"): void {
    if (this.nodes.has(id)) return;
    const { sf } = unit;
    const code = located.getText(sf);
    this.add({
      id, kind, name, parentId, file: unit.file,
      startLine: line(sf, located.getStart(sf)), endLine: line(sf, located.getEnd()),
      code, codeHash: hashCode(code), guards: [],
    });
    this.register(owner, id);
    this.owners.set(owner, id);
    this.names.add(name);
  }

  private classNode(unit: Unit, cls: ts.ClassLikeDeclaration, alias?: string): void {
    const name = cls.name?.text ?? alias ?? "default";
    const id = `${unit.file}#${name}`;
    if (this.nodes.has(id)) return;
    const { sf } = unit;
    this.add({ id, kind: "Type", name, parentId: unit.fileId, file: unit.file, startLine: line(sf, cls.getStart(sf)), endLine: line(sf, cls.getEnd()), guards: [] });
    this.register(cls, id);
    this.owners.set(cls, id);
    this.names.add(name);

    for (const member of cls.members) {
      const memberName = ts.isConstructorDeclaration(member)
        ? "constructor"
        : ts.isGetAccessor(member)
          ? `get ${propertyName(member.name, sf)}`
          : ts.isSetAccessor(member)
            ? `set ${propertyName(member.name, sf)}`
            : propertyName(member.name, sf);
      if (!memberName) continue;
      const isFunction =
        ((ts.isMethodDeclaration(member) || ts.isConstructorDeclaration(member) || ts.isAccessor(member)) && member.body !== undefined) ||
        (ts.isPropertyDeclaration(member) && member.initializer !== undefined && isFunctionValue(unwrap(member.initializer)));
      if (isFunction) this.method(unit, `${id}.${memberName}`, memberName.replace(/^(get|set) /, ""), member, member, id);
    }
  }

  /** `export const tradeService = { async create() {…} }`: an object of functions reads as a type with methods. */
  private objectNode(unit: Unit, name: string, decl: ts.VariableDeclaration, init: ts.ObjectLiteralExpression, located: ts.Node): void {
    const id = `${unit.file}#${name}`;
    if (this.nodes.has(id)) return;
    const { sf } = unit;
    this.add({ id, kind: "Type", name, parentId: unit.fileId, file: unit.file, startLine: line(sf, located.getStart(sf)), endLine: line(sf, located.getEnd()), guards: [] });
    this.register(decl, id);
    this.owners.set(decl, id);
    for (const property of init.properties) {
      const fn = memberFunction(property);
      const memberName = propertyName(property.name, sf);
      if (fn && memberName) this.method(unit, `${id}.${memberName}`, memberName, property, property, id);
    }
  }

  /** node:test, Vitest, Jest and Playwright tests: test("…", fn) and it("…", fn), named with their describe blocks. */
  private collectTests(unit: Unit): void {
    const { sf, file } = unit;
    const visit = (node: ts.Node, suites: string[]): void => {
      if (ts.isCallExpression(node) && node.arguments.length >= 2) {
        const callee = calleeRoot(node.expression);
        const title = stringValue(node.arguments[0]);
        const body = node.arguments[node.arguments.length - 1];
        if (callee && title !== undefined && isFunctionValue(body)) {
          if (SUITE_CALLS.has(callee)) {
            ts.forEachChild(body, (child) => visit(child, [...suites, title]));
            return;
          }

          if (TEST_CALLS.has(callee)) {
            const name = [...suites, title].join(" › ");
            let id = `${file}#test:${name}`;
            for (let n = 2; this.nodes.has(id); n++) id = `${file}#test:${name} (${n})`;
            this.method(unit, id, name, body, node, unit.fileId, "Test");
            return;
          }
        }
      }

      ts.forEachChild(node, (child) => visit(child, suites));
    };
    visit(sf, []);
  }

  // ---------- endpoints ----------

  private collectEndpoints(unit: Unit, kind: NextFileKind, urlPath: string): void {
    const { sf } = unit;
    if (kind === "page" || kind === "pages-page" || kind === "pages-api") {
      const handler = defaultExport(sf);
      if (!handler) return;
      const verb = kind === "pages-api" ? "ANY" : "GET";
      this.endpoints.push({ route: `${verb} ${urlPath}`, unit, handler: handler.handler, at: handler.at, kind: kind === "pages-api" ? "route" : "page" });
      return;
    }

    for (const [verb, handler] of namedExports(sf)) {
      if ((HTTP_VERBS as readonly string[]).includes(verb)) this.endpoints.push({ route: `${verb} ${urlPath}`, unit, handler: handler.handler, at: handler.at, kind: "route" });
    }
  }

  /** A server action is a POST any page can make: it is an endpoint like a route handler. */
  private action(unit: Unit, name: string, handler: ts.Node): void {
    const stem = path.posix.basename(unit.file).replace(/\.[^.]+$/, "");
    this.endpoints.push({ route: `ACTION ${stem}/${name}`, unit, handler, at: handler, kind: "action" });
  }

  private addEndpoint(spec: EndpointSpec): void {
    const { unit, route } = spec;
    const { sf } = unit;
    let id = `endpoint:${route}`;
    if (this.nodes.has(id)) id = `endpoint:${route} (${unit.pkg.name})`;
    if (this.nodes.has(id)) return;

    const target = spec.handler ? this.resolveHandler(spec.handler, unit) : undefined;
    const body = target ? this.bodies.get(target) : undefined;
    const guards = [...new Set([...(body ? guardsIn(body) : []), ...(spec.handler ? wrapperGuards(spec.handler) : [])])].sort();
    if (spec.kind === "page") this.pages.add(id);
    this.add({
      id, kind: "Endpoint", name: route, parentId: unit.fileId, file: unit.file,
      startLine: line(sf, spec.at.getStart(sf)), endLine: line(sf, spec.at.getEnd()), route, guards,
    });
    if (target) this.edges.push({ from: id, to: target, kind: "HandledBy" });
  }

  /** The graph node that handles an endpoint: the declaration itself, or what an alias or `export { x as GET }` points at. */
  private resolveHandler(handler: ts.Node, unit: Unit): string | undefined {
    const direct = this.declared.get(declKey(handler));
    if (direct) return direct;
    const name = ts.isVariableDeclaration(handler) || ts.isFunctionDeclaration(handler) ? handler.name : ts.isIdentifier(handler) ? handler : undefined;
    const value = ts.isVariableDeclaration(handler) && handler.initializer ? unwrap(handler.initializer) : undefined;
    const lookup = value && (ts.isIdentifier(value) || ts.isPropertyAccessExpression(value)) ? value : name;
    if (!lookup) return undefined;
    return this.targetsOf(lookup, unit.checker)[0];
  }

  // ---------- pass 2: edges ----------

  private collectEdges(unit: Unit): void {
    const { sf, checker } = unit;
    const visit = (node: ts.Node, owner: string): void => {
      const own = this.owners.get(node) ?? owner;

      if (ts.isImportDeclaration(node) || ts.isExportDeclaration(node)) {
        if (node.moduleSpecifier) this.linkImport(unit, node.moduleSpecifier);
        return;
      }

      if (ts.isCallExpression(node) && node.expression.kind === ts.SyntaxKind.ImportKeyword && node.arguments[0]) {
        this.linkImport(unit, node.arguments[0]);
      }

      if (ts.isExportAssignment(node) && ts.isIdentifier(node.expression)) return;

      if (ts.isHeritageClause(node)) {
        for (const type of node.types) {
          for (const to of this.targetsOf(type.expression, checker)) this.edges.push({ from: own, to, kind: "Implements" });
        }
        return;
      }

      if (ts.isIdentifier(node) && this.names.has(node.text) && !isDeclarationName(node)) {
        const from = this.nodes.get(own);
        for (const to of this.targetsOf(node, checker)) {
          const target = this.nodes.get(to);
          if (!target || !from) continue;
          const kind: EdgeKind = from.kind === "Test" && target.kind !== "Test" ? "Covers" : "Calls";
          this.edges.push({ from: own, to, kind });
        }
      }

      ts.forEachChild(node, (child) => visit(child, own));
    };
    visit(sf, unit.fileId);
  }

  /** A file imports another file of the repo: the importing file depends on it. */
  private linkImport(unit: Unit, specifier: ts.Expression): void {
    const symbol = unit.checker.getSymbolAtLocation(specifier);
    const target = symbol?.valueDeclaration ?? symbol?.declarations?.[0];
    if (!target || !ts.isSourceFile(target)) return;
    const to = `file:${rel(this.root, path.resolve(target.fileName))}`;
    if (this.nodes.has(to)) this.edges.push({ from: unit.fileId, to, kind: "DependsOn" });
  }

  /** The graph nodes an identifier refers to, following imports and re-exports to the declaration. */
  private targetsOf(node: ts.Node, checker: ts.TypeChecker): string[] {
    let symbol = ts.isShorthandPropertyAssignment(node.parent) && node.parent.name === node
      ? checker.getShorthandAssignmentValueSymbol(node.parent)
      : checker.getSymbolAtLocation(node);
    if (symbol && symbol.flags & ts.SymbolFlags.Alias) {
      try {
        symbol = checker.getAliasedSymbol(symbol);
      } catch {
        return [];
      }
    }

    const ids = new Set<string>();
    for (const decl of symbol?.declarations ?? []) {
      let id = this.declared.get(declKey(decl));
      // `new Service()` lands on its constructor when the class has one.
      if (id && ts.isClassLike(decl) && ts.isNewExpression(node.parent) && this.nodes.has(`${id}.constructor`)) id = `${id}.constructor`;
      else if (id && this.nodes.get(id)?.kind === "Type" && !ts.isHeritageClause(node.parent?.parent)) continue;
      if (id) ids.add(id);
    }
    return [...ids];
  }

  /** Next.js middleware (proxy.ts since Next 16) runs before every page, route and action it matches. */
  private linkPipeline(units: Unit[]): void {
    for (const unit of units.filter((u) => isMiddlewareFile(u.inner))) {
      const handler = namedExports(unit.sf).get("middleware") ?? namedExports(unit.sf).get("proxy") ?? defaultExport(unit.sf);
      if (!handler) continue;
      const pipelineId = `pipeline:${unit.pkg.name}`;
      const name = unit.inner.replace(/^src\//, "").replace(/\.[^.]+$/, "");
      const middlewareId = `middleware:${unit.pkg.name}:${name}`;
      const { sf } = unit;
      const at = handler.at;
      this.add({ id: pipelineId, kind: "Feature", name: "Request pipeline", parentId: this.nodes.get(unit.fileId)?.parentId ?? APP_ID, file: unit.file, guards: [] });
      this.add({
        id: middlewareId, kind: "Middleware", name, parentId: pipelineId, file: unit.file,
        startLine: line(sf, at.getStart(sf)), endLine: line(sf, at.getEnd()), code: at.getText(sf), codeHash: hashCode(at.getText(sf)),
        guards: matchers(sf),
      });

      const target = handler.handler ? this.resolveHandler(handler.handler, unit) : undefined;
      if (target) this.edges.push({ from: middlewareId, to: target, kind: "HandledBy" });
      for (const endpoint of this.nodes.values()) {
        if (endpoint.kind === "Endpoint" && endpoint.file && (unit.pkg.dir === "" || endpoint.file.startsWith(`${unit.pkg.dir}/`))) {
          this.edges.push({ from: middlewareId, to: endpoint.id, kind: "Next" });
        }
      }
    }
  }

  // ---------- helpers ----------

  /** The graph node a piece of code belongs to: the innermost function, test or type around it, else its file. */
  private ownerOf(node: ts.Node, unit: Unit): string {
    for (let current: ts.Node | undefined = node; current; current = current.parent) {
      const id = this.owners.get(current);
      if (id) return id;
    }
    return unit.fileId;
  }

  private register(node: ts.Node, id: string): void {
    this.declared.set(declKey(node), id);
    this.bodies.set(id, node);
  }

  private add(node: CodeNode): void {
    if (!this.nodes.has(node.id)) this.nodes.set(node.id, node);
  }
}

// ---------- syntax helpers ----------

function declKey(node: ts.Node): string {
  return `${path.resolve(node.getSourceFile().fileName)}#${node.pos}`;
}

function line(sf: ts.SourceFile, pos: number): number {
  return sf.getLineAndCharacterOfPosition(pos).line + 1;
}

function unwrap(e: ts.Expression): ts.Expression {
  while (ts.isParenthesizedExpression(e) || ts.isAsExpression(e) || ts.isSatisfiesExpression(e) || ts.isTypeAssertionExpression(e) || ts.isNonNullExpression(e)) {
    e = e.expression;
  }
  return e;
}

/** A function, or a call wrapping one: memo(() => …), forwardRef(…), cache(async () => …), withAuth(handler). */
function isFunctionValue(e: ts.Node): boolean {
  if (ts.isArrowFunction(e) || ts.isFunctionExpression(e)) return true;
  return ts.isCallExpression(e) && e.arguments.some((a) => isFunctionValue(unwrap(a)));
}

function memberFunction(p: ts.ObjectLiteralElementLike): ts.Node | undefined {
  if (ts.isMethodDeclaration(p) && p.body) return p;
  if (ts.isPropertyAssignment(p) && isFunctionValue(unwrap(p.initializer))) return p;
  return undefined;
}

function propertyName(name: ts.PropertyName | undefined, sf: ts.SourceFile): string | undefined {
  if (!name) return undefined;
  if (ts.isIdentifier(name) || ts.isPrivateIdentifier(name) || ts.isStringLiteral(name) || ts.isNumericLiteral(name)) return name.text;
  return name.getText(sf);
}

function isExported(node: ts.Node): boolean {
  return ts.canHaveModifiers(node) && (ts.getModifiers(node)?.some((m) => m.kind === ts.SyntaxKind.ExportKeyword) ?? false);
}

function hasDirective(sf: ts.SourceFile, directive: string): boolean {
  for (const statement of sf.statements) {
    if (!ts.isExpressionStatement(statement) || !ts.isStringLiteral(statement.expression)) return false;
    if (statement.expression.text === directive) return true;
  }
  return false;
}

function isDeclarationName(node: ts.Identifier): boolean {
  const parent = node.parent;
  if (ts.isImportSpecifier(parent) || ts.isExportSpecifier(parent) || ts.isImportClause(parent) || ts.isNamespaceImport(parent)) return true;
  const named =
    ts.isFunctionDeclaration(parent) || ts.isVariableDeclaration(parent) || ts.isClassLike(parent) || ts.isMethodDeclaration(parent) ||
    ts.isPropertyAssignment(parent) || ts.isPropertyDeclaration(parent) || ts.isParameter(parent) || ts.isBindingElement(parent);
  return named && parent.name === node;
}

/** "test" for test(…), test.only(…), test.each(…)(…). */
function calleeRoot(e: ts.Expression): string | undefined {
  if (ts.isIdentifier(e)) return e.text;
  if (ts.isPropertyAccessExpression(e)) return calleeRoot(e.expression);
  if (ts.isCallExpression(e)) return calleeRoot(e.expression);
  return undefined;
}

function stringValue(e: ts.Expression): string | undefined {
  if (ts.isStringLiteral(e) || ts.isNoSubstitutionTemplateLiteral(e)) return e.text;
  if (ts.isTemplateExpression(e)) return e.getText().slice(1, -1);
  return undefined;
}

interface Exported {
  /** What handles it: a declaration, or the identifier it re-exports. */
  handler: ts.Node | undefined;
  /** Where it is written, for line numbers. */
  at: ts.Node;
}

/** Named exports of a file: functions, consts (destructured too) and `export { a as GET }`. */
function namedExports(sf: ts.SourceFile): Map<string, Exported> {
  const found = new Map<string, Exported>();
  for (const statement of sf.statements) {
    if (ts.isFunctionDeclaration(statement) && statement.name && isExported(statement)) {
      found.set(statement.name.text, { handler: statement, at: statement });
    } else if (ts.isVariableStatement(statement) && isExported(statement)) {
      for (const decl of statement.declarationList.declarations) {
        if (ts.isIdentifier(decl.name)) {
          found.set(decl.name.text, { handler: decl, at: statement });
        } else if (ts.isObjectBindingPattern(decl.name)) {
          for (const element of decl.name.elements) {
            if (ts.isIdentifier(element.name)) found.set(element.name.text, { handler: undefined, at: statement });
          }
        }
      }
    } else if (ts.isExportDeclaration(statement) && !statement.moduleSpecifier && statement.exportClause && ts.isNamedExports(statement.exportClause)) {
      for (const element of statement.exportClause.elements) {
        found.set(element.name.text, { handler: element.propertyName ?? element.name, at: statement });
      }
    }
  }
  return found;
}

/** The default export: `export default function Page()`, `export default Page`, or `export { Page as default }`. */
function defaultExport(sf: ts.SourceFile): Exported | undefined {
  for (const statement of sf.statements) {
    if (ts.isFunctionDeclaration(statement) && ts.getModifiers(statement)?.some((m) => m.kind === ts.SyntaxKind.DefaultKeyword)) {
      return { handler: statement, at: statement };
    }

    if (ts.isClassDeclaration(statement) && ts.getModifiers(statement)?.some((m) => m.kind === ts.SyntaxKind.DefaultKeyword)) {
      return { handler: statement, at: statement };
    }

    if (ts.isExportAssignment(statement) && !statement.isExportEquals) {
      const value = unwrap(statement.expression);
      return { handler: ts.isIdentifier(value) ? value : statement, at: statement };
    }
  }
  return namedExports(sf).get("default");
}

/** Guards a handler applies itself: calls like auth(), requireUserId(), checkRateLimit(), or a secret header check. */
function guardsIn(body: ts.Node): string[] {
  const guards: string[] = [];
  const visit = (node: ts.Node): void => {
    if (ts.isCallExpression(node)) {
      const callee = ts.isIdentifier(node.expression)
        ? node.expression.text
        : ts.isPropertyAccessExpression(node.expression)
          ? node.expression.name.text
          : undefined;
      const argument = node.arguments[0] ? stringValue(node.arguments[0]) : undefined;
      const guard = callee ? guardOf(callee, argument) : undefined;
      if (guard) guards.push(guard);
    }
    ts.forEachChild(node, visit);
  };
  visit(body);
  return guards;
}

/** `export const POST = withAuth(handler)`: the wrapper is the guard. */
function wrapperGuards(handler: ts.Node): string[] {
  if (!ts.isVariableDeclaration(handler) || !handler.initializer) return [];
  const guards: string[] = [];
  for (let e = unwrap(handler.initializer); ts.isCallExpression(e); e = unwrap(e.arguments[0] ?? e.expression)) {
    const name = ts.isIdentifier(e.expression) ? e.expression.text : ts.isPropertyAccessExpression(e.expression) ? e.expression.name.text : undefined;
    const guard = name ? guardOf(name, undefined) : undefined;
    if (guard) guards.push(guard);
    if (!e.arguments[0]) break;
  }
  return guards;
}

/** `export const config = { matcher: [...] }` in middleware: which paths it runs on. */
function matchers(sf: ts.SourceFile): string[] {
  for (const statement of sf.statements) {
    if (!ts.isVariableStatement(statement)) continue;
    for (const decl of statement.declarationList.declarations) {
      if (!ts.isIdentifier(decl.name) || decl.name.text !== "config" || !decl.initializer) continue;
      const init = unwrap(decl.initializer);
      if (!ts.isObjectLiteralExpression(init)) continue;
      for (const p of init.properties) {
        if (!ts.isPropertyAssignment(p) || propertyName(p.name, sf) !== "matcher") continue;
        const value = unwrap(p.initializer);
        const items = ts.isArrayLiteralExpression(value) ? value.elements : [value];
        return items.map((i) => stringValue(i)).filter((s): s is string => s !== undefined).map((s) => `matcher:${s}`);
      }
    }
  }
  return [];
}

function packageName(root: string, dir: string): string {
  const file = path.join(root, dir, "package.json");
  if (existsSync(file)) {
    try {
      const name = (JSON.parse(readFileSync(file, "utf8")) as { name?: unknown }).name;
      if (typeof name === "string" && name.length > 0) return name;
    } catch {
      // A broken package.json still names the folder.
    }
  }
  return dir === "" ? path.basename(root) : dir;
}
