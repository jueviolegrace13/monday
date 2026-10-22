import { formatMoney } from "@/lib/money";

export function MoneyLabel({ value }: { value: number }) {
  return formatMoney(value);
}

export function NoteHtml({ html }: { html: string }) {
  return <div dangerouslySetInnerHTML={{ __html: html }} />;
}
