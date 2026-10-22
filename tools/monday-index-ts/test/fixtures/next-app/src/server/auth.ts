export async function auth(): Promise<{ userId: string } | null> {
  return null;
}

export async function requireUserId(): Promise<string> {
  const session = await auth();
  if (!session) throw new Error("Unauthorized");
  return session.userId;
}
