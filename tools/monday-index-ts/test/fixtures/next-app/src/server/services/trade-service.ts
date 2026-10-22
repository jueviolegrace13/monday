import { round } from "@/lib/money";

export class TradeStore {
  constructor(private readonly prefix: string) {}

  key(id: string): string {
    return `${this.prefix}:${id}`;
  }
}

export const tradeService = {
  async get(userId: string, id: string) {
    return { userId, id, pnl: round(1.234) };
  },
  remove: async (userId: string, id: string) => {
    const store = new TradeStore("trades");
    return store.key(`${userId}/${id}`);
  },
};
