"use server";

import { requireUserId } from "@/server/auth";
import { tradeService } from "@/server/services/trade-service";

export async function deleteTradeAction(id: string) {
  const userId = await requireUserId();
  return tradeService.remove(userId, id);
}

export const publicAction = async (id: string) => tradeService.remove("anyone", id);
