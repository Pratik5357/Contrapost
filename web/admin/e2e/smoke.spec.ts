import { expect, test, type Page } from "@playwright/test";

// The smoke path from design doc §5.2: log in, open an account, page its history, open a
// reconciliation run. Needs a stack seeded by e2e/seed.sh.

const username = process.env.ADMIN_USERNAME ?? "admin";
const password = process.env.ADMIN_PASSWORD ?? "contrapost-admin";

async function logIn(page: Page) {
  await page.goto("/login");
  await page.getByLabel("Username").fill(username);
  await page.getByLabel("Password").fill(password);
  await page.getByRole("button", { name: "Log in" }).click();
  await expect(page.getByRole("heading", { name: "Overview" })).toBeVisible();
}

test("pages need a session, and a wrong password is refused", async ({ page }) => {
  await page.goto("/accounts");
  await expect(page).toHaveURL(/\/login$/);

  await page.getByLabel("Username").fill(username);
  await page.getByLabel("Password").fill("not-the-password");
  await page.getByRole("button", { name: "Log in" }).click();
  await expect(page.getByText("Wrong username or password.")).toBeVisible();
});

test("overview shows balanced totals and a clean reconciliation", async ({ page }) => {
  await logIn(page);
  const inr = page.getByRole("row", { name: /^INR/ });
  await expect(inr.getByText("clean")).toBeVisible(); // I2: debits equal credits
  await expect(page.getByText("Latest reconciliation")).toBeVisible();
});

test("open an account and page its history", async ({ page }) => {
  await logIn(page);
  await page.getByRole("link", { name: "Accounts" }).click();
  await page.getByLabel("Code contains").fill("wallet:smoke");
  await page.getByRole("button", { name: "Search" }).click();
  await page.getByRole("link", { name: "wallet:smoke" }).first().click();

  await expect(page.getByRole("heading", { name: "wallet:smoke" })).toBeVisible();
  await expect(page.getByRole("heading", { name: "Open holds" })).toBeVisible();
  const entries = page.locator("table").last().locator("tbody tr");
  await expect(entries).toHaveCount(20);
  await expect(entries.first().locator("td").first()).toHaveText("28");

  await page.getByRole("link", { name: "Next page →" }).click();
  await expect(page).toHaveURL(/after=/);
  await expect(entries).toHaveCount(8);
  await expect(entries.last().locator("td").first()).toHaveText("1");
  await expect(page.getByRole("link", { name: "Next page →" })).toHaveCount(0);

  // From an entry to its transaction.
  await entries.first().getByRole("link").click();
  await expect(page.getByRole("heading", { name: "Legs" })).toBeVisible();
  await expect(page.getByText("transfer.posted")).toBeVisible();
});

test("open a reconciliation run", async ({ page }) => {
  await logIn(page);
  await page.getByRole("link", { name: "Reconciliation", exact: true }).click();
  await page.locator("tbody tr").first().getByRole("link").click();
  await expect(page.getByRole("heading", { name: "Reconciliation run" })).toBeVisible();
  await expect(page.getByText("No findings: the ledger agreed with itself.")).toBeVisible();
});

test("log out ends the session", async ({ page }) => {
  await logIn(page);
  await page.getByRole("button", { name: "Log out" }).click();
  await expect(page).toHaveURL(/\/login$/);
  await page.goto("/outbox");
  await expect(page).toHaveURL(/\/login$/);
});
