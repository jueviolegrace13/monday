import { auth } from "@/server/auth";
import { tradeService } from "@/server/services/trade-service";

export async function GET(_request: Request, { params }: { params: { id: string } }) {
  const session = await auth();
  if (!session) return new Response(null, { status: 401 });
  return Response.json(await tradeService.get(session.userId, params.id));
}

export async function DELETE(_request: Request, { params }: { params: { id: string } }) {
  return Response.json(await tradeService.remove("anyone", params.id));
}
