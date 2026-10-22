export function proxy(request: Request) {
  return request.url.length > 0 ? null : undefined;
}

export const config = { matcher: ["/((?!_next/static).*)"] };
