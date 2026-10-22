import ts from "typescript";
import type { CodeNode, SecurityFinding, Severity } from "./graph.ts";
import { WRITE_VERBS, isSensitiveRoute } from "./next-routes.ts";

// Static threat rules for TypeScript and Next.js, the same rule ids as the C# indexer where the
// risk is the same. Rules are deliberately conservative: a finding is a prompt to look, and every
// rule says what would clear it.

const AUTH_CALL = /^(auth|getServerSession|getSession|currentUser|getCurrentUser|getUser|verifySession|validateRequest|withAuth|requireAuth)$|^require[A-Z]/;
const RATE_LIMIT_CALL = /^(check|apply|enforce|consume|with)?rate.?limit(er)?$|^limit$/i;
const SECRET_HEADER = /^(authorization|x-[a-z-]*(key|secret|token|signature))$/i;

const SECRET_NAME = /(password|passwd|pwd|secret|apikey|api_key|accesskey|access_key|token|connectionstring|privatekey|private_key|clientsecret)/i;
const SECRET_VALUE = /(-----BEGIN [A-Z ]*PRIVATE KEY-----|AKIA[0-9A-Z]{16}|\bsk-[A-Za-z0-9_-]{20,}|\bsk_live_[A-Za-z0-9]{16,}|ghp_[A-Za-z0-9]{30,}|xox[baprs]-[A-Za-z0-9-]{10,}|(Password|Pwd)\s*=\s*[^;{\s]{4,}|postgres(ql)?:\/\/[^:\s]+:[^@\s]+@)/i;
const RAW_SQL = new Set(["$queryRawUnsafe", "$executeRawUnsafe"]);
const WEAK_HASHES = new Set(["md5", "sha1"]);

/**
 * The guard a call stands for, or undefined. Auth guards count as sign-in; rate limiters as limits.
 * `headers.get("authorization")` is a secret checked by hand, as cron and webhook routes do.
 */
export function guardOf(callee: string, firstArgument: string | undefined): string | undefined {
  if (AUTH_CALL.test(callee)) return `Auth:${callee}`;
  if (RATE_LIMIT_CALL.test(callee)) return `RateLimit:${callee}`;
  if (callee === "get" && firstArgument !== undefined && SECRET_HEADER.test(firstArgument)) return `Auth:header:${firstArgument.toLowerCase()}`;
  return undefined;
}

const isAuth = (guard: string): boolean => guard.startsWith("Auth:");
const isLimit = (guard: string): boolean => guard.startsWith("RateLimit:");

function finding(node: CodeNode, rule: string, severity: Severity, title: string, why: string, fix: string): SecurityFinding {
  return { rule, severity, nodeId: node.id, title, why, fix, file: node.file, line: node.startLine };
}

/**
 * Route handlers and server actions (pages are left out: they only read, and the middleware decides who sees them).
 * `middlewareLimits` is true when the middleware calls a rate limiter, which may cover sign-in routes.
 */
export function endpointFindings(nodes: CodeNode[], middlewareLimits: boolean): SecurityFinding[] {
  const findings: SecurityFinding[] = [];
  for (const ep of nodes.filter((n) => n.kind === "Endpoint" && n.route)) {
    const route = ep.route!;
    const verb = route.split(" ")[0];
    const write = WRITE_VERBS.has(verb) || verb === "ANY";
    const authed = ep.guards.some(isAuth);
    const limited = ep.guards.some(isLimit);
    const sensitive = isSensitiveRoute(route);

    if (write && !authed) {
      findings.push(
        sensitive
          ? finding(ep, "write-without-auth", "Medium", `${route} changes data without sign-in`,
              "It looks like a sign-in or account route, which has to be public; anyone can call it.",
              "Confirm it must be public, and that it checks its input and limits attempts.")
          : finding(ep, "write-without-auth", "High", `${route} changes data without sign-in`,
              "Anyone who can reach the app can call it. Writes are the first thing an attacker tries.",
              "Check the session at the top of the handler (auth() or the project's require… helper) before doing any work."),
      );
    }

    if (!authed && !limited && verb !== "ACTION") {
      findings.push(finding(ep, "public-without-rate-limit", write ? "Medium" : "Low", `${route} is public and has no rate limit`,
        "A public route without a limit can be flooded to slow the app down or to run up hosting costs.",
        "Rate-limit it by client (in the handler or in the middleware)."));
    }

    if (sensitive && !limited && !middlewareLimits) {
      findings.push(finding(ep, "credential-route-without-rate-limit", "Medium", `${route} looks like a sign-in or token route but has no rate limit`,
        "Without a limit, passwords and codes can be guessed by brute force.",
        "Rate-limit by user and by IP, and lock out after repeated failures."));
    }
  }
  return findings;
}

/** The variable, property or parameter name a literal is assigned to, if any. */
function holderName(literal: ts.Node): string | undefined {
  const parent = literal.parent;
  if ((ts.isVariableDeclaration(parent) || ts.isPropertyAssignment(parent) || ts.isPropertyDeclaration(parent) || ts.isParameter(parent)) && parent.name && ts.isIdentifier(parent.name)) {
    return parent.name.text;
  }

  if (ts.isBinaryExpression(parent) && parent.operatorToken.kind === ts.SyntaxKind.EqualsToken) {
    return parent.left.getText().split(".").pop();
  }

  return undefined;
}

/** A value that reads like a key: letters and digits mixed, no spaces, paths, dots or URLs. */
function looksLikeKey(value: string): boolean {
  return value.length >= 12 && /[A-Za-z]/.test(value) && /\d/.test(value) && !/[\s/.:]/.test(value);
}

function isBuiltString(e: ts.Expression): boolean {
  return ts.isTemplateExpression(e) || (ts.isBinaryExpression(e) && e.operatorToken.kind === ts.SyntaxKind.PlusToken);
}

/** Threats read from the code itself, attached to the function (or file) they are in. */
export function codeFindings(sf: ts.SourceFile, file: string, ownerOf: (node: ts.Node) => string, isTest: boolean): SecurityFinding[] {
  const findings: SecurityFinding[] = [];
  const add = (at: ts.Node, rule: string, severity: Severity, title: string, why: string, fix: string): void => {
    findings.push({ rule, severity, nodeId: ownerOf(at), title, why, fix, file, line: sf.getLineAndCharacterOfPosition(at.getStart(sf)).line + 1 });
  };

  const visit = (node: ts.Node): void => {
    if (ts.isCallExpression(node)) {
      const callee = ts.isPropertyAccessExpression(node.expression) ? node.expression.name.text : ts.isIdentifier(node.expression) ? node.expression.text : undefined;
      const first = node.arguments[0];

      if (callee && RAW_SQL.has(callee) && first && !ts.isStringLiteral(first) && !ts.isNoSubstitutionTemplateLiteral(first)) {
        add(node, "sql-built-from-strings", "High", "SQL is built from strings",
          "If any part comes from a request, an attacker can change the query and read or delete data.",
          "Use $queryRaw with a tagged template (values become parameters), or the Prisma query API.");
      } else if (callee === "query" && first && isBuiltString(first)) {
        add(node, "sql-built-from-strings", "High", "A query is built from strings",
          "If any part comes from a request, an attacker can change the query and read or delete data.",
          "Pass values as parameters ($1, $2…) instead of putting them in the text.");
      } else if (callee === "createHash" && first && ts.isStringLiteral(first) && WEAK_HASHES.has(first.text.toLowerCase())) {
        add(node, "weak-hash", "Medium", `${first.text.toUpperCase()} is used for hashing`,
          "MD5 and SHA-1 collide easily; they don't protect passwords, signatures or tokens.",
          "Use SHA-256 for checksums, and bcrypt, scrypt or Argon2 for passwords.");
      } else if (ts.isIdentifier(node.expression) && node.expression.text === "eval") {
        add(node, "code-from-strings", "High", "eval runs code built at run time",
          "If any part of the string comes from a user, they can run their own code on the server or in the browser.",
          "Remove eval; parse data with JSON.parse or a real parser.");
      }
    }

    if (ts.isJsxAttribute(node) && node.name.getText(sf) === "dangerouslySetInnerHTML") {
      const value = node.initializer?.getText(sf) ?? "";
      if (!/saniti[sz]e|purify/i.test(value)) {
        add(node, "html-from-data", "Medium", "HTML is inserted without sanitizing it here",
          "If the HTML holds anything a user wrote, a script in it runs in other users' browsers (XSS).",
          "Sanitize where it is inserted (sanitize-html, DOMPurify), or check it is sanitized before it is stored.");
      }
    }

    if (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) {
      const value = node.text;
      const holder = holderName(node);
      if (value.length >= 8 && (SECRET_VALUE.test(value) || (holder !== undefined && SECRET_NAME.test(holder) && looksLikeKey(value)))) {
        add(node, "hardcoded-secret", isTest ? "Medium" : "High",
          holder ? `'${holder}' holds a value written in the code` : "A secret-looking value is written in the code",
          "Anyone with the code (or a leaked copy) gets the secret, and it can't be rotated without a release.",
          "Read it from the environment (process.env) or a secret store; rotate the exposed value.");
      }
    }

    ts.forEachChild(node, visit);
  };
  visit(sf);
  return findings;
}
