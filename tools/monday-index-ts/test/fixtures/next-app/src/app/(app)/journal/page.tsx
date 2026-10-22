import { requireUserId } from "@/server/auth";
import { MoneyLabel } from "@/components/money-label";

export default async function JournalPage() {
  await requireUserId();
  return <MoneyLabel value={3} />;
}
