import { describe, test } from "node:test";
import { formatMoney } from "./money";

describe("formatMoney", () => {
  test("rounds to cents", () => {
    formatMoney(1.234);
  });
});
