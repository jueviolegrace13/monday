// Where a Next.js file sits decides what it serves. These helpers turn a file path into the route a
// request uses, written like Monday's C# routes: "GET /api/journal/{id}".

export const HTTP_VERBS = ["GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS"] as const;

export const WRITE_VERBS = new Set(["POST", "PUT", "PATCH", "DELETE", "ACTION"]);

export type NextFileKind = "route" | "page" | "pages-api" | "pages-page";

export interface NextFile {
  kind: NextFileKind;
  /** The URL path, e.g. "/api/journal/{id}". */
  path: string;
}

const SCRIPT = String.raw`\.(?:ts|tsx|js|jsx|mjs|cjs)$`;
const APP_FILE = new RegExp(String.raw`^(?:src/)?app/(?:(.*)/)?(route|page)${SCRIPT}`);
const PAGES_FILE = new RegExp(String.raw`^(?:src/)?pages/(.*)${SCRIPT}`);

/** One folder or file name as a route segment, or null when it adds nothing to the URL. */
function segment(name: string): string | null {
  if (/^\(.*\)$/.test(name) || name.startsWith("@")) return null; // route groups and parallel slots
  const name2 = name.replace(/^(\(\.{1,3}\))+/, ""); // intercepting routes: (.)photo -> photo
  const optionalCatchAll = /^\[\[\.\.\.(.+)\]\]$/.exec(name2);
  if (optionalCatchAll) return `{*${optionalCatchAll[1]}?}`;
  const catchAll = /^\[\.\.\.(.+)\]$/.exec(name2);
  if (catchAll) return `{*${catchAll[1]}}`;
  const dynamic = /^\[(.+)\]$/.exec(name2);
  if (dynamic) return `{${dynamic[1]}}`;
  return name2;
}

function join(segments: string[]): string {
  const kept = segments.map(segment).filter((s): s is string => s !== null && s.length > 0);
  return `/${kept.join("/")}`;
}

/**
 * What a file serves, from its path inside its package ("src/app/api/x/route.ts"), or null.
 * Folders starting with "_" are private in the app router and are never routes.
 */
export function nextFile(packagePath: string): NextFile | null {
  const app = APP_FILE.exec(packagePath);
  if (app) {
    const folders = app[1] ? app[1].split("/") : [];
    if (folders.some((f) => f.startsWith("_"))) return null;
    return { kind: app[2] === "route" ? "route" : "page", path: join(folders) };
  }

  const pages = PAGES_FILE.exec(packagePath);
  if (pages) {
    const parts = pages[1].split("/");
    if (parts[0] === "api") {
      const last = parts[parts.length - 1] === "index" ? parts.slice(0, -1) : parts;
      return { kind: "pages-api", path: join(last) };
    }

    if (/^_(app|document|error)$/.test(parts[parts.length - 1]) || parts.some((p) => p.startsWith("_"))) return null;
    const last = parts[parts.length - 1] === "index" ? parts.slice(0, -1) : parts;
    return { kind: "pages-page", path: join(last) };
  }

  return null;
}

/** Next.js middleware (or "proxy" since Next 16) at the package root or in src. */
export function isMiddlewareFile(packagePath: string): boolean {
  return new RegExp(String.raw`^(?:src/)?(?:middleware|proxy)${SCRIPT}`).test(packagePath);
}

const SENSITIVE_ROUTE = /(log-?in|sign-?in|token|auth|password|otp|verify|2fa|register|sign-?up|invite)/i;

/** A route that handles credentials, codes or invitations: it has to be public. */
export function isSensitiveRoute(route: string): boolean {
  return SENSITIVE_ROUTE.test(route);
}
