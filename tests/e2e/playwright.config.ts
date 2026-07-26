import { defineConfig, devices } from "@playwright/test";

const gatewayUrl = process.env.E2E_GATEWAY_URL ?? "http://127.0.0.1:8080";
const keycloakUrl = process.env.E2E_KEYCLOAK_URL ?? "http://localhost:8180";
const issuer = `${keycloakUrl}/realms/ticketflow`;

function authEnv(app: "customer" | "organizer" | "scanner") {
  const values = {
    customer: {
      url: "http://localhost:3000",
      secret: "phase12-customer-auth-secret-change-me",
      id: "ticketflow-customer-web",
      clientSecret: "customer-web-dev-secret",
    },
    organizer: {
      url: "http://localhost:3001",
      secret: "phase12-organizer-auth-secret-change-me",
      id: "ticketflow-organizer-dashboard",
      clientSecret: "organizer-dashboard-dev-secret",
    },
    scanner: {
      url: "http://localhost:3002",
      secret: "phase12-scanner-auth-secret-change-me",
      id: "ticketflow-gate-scanner",
      clientSecret: "gate-scanner-dev-secret",
    },
  }[app];

  return {
    AUTH_URL: values.url,
    AUTH_SECRET: values.secret,
    AUTH_KEYCLOAK_ISSUER: issuer,
    AUTH_KEYCLOAK_ID: values.id,
    AUTH_KEYCLOAK_SECRET: values.clientSecret,
    GATEWAY_URL: gatewayUrl,
    NEXT_PUBLIC_GATEWAY_URL: gatewayUrl,
  };
}

export default defineConfig({
  testDir: ".",
  outputDir: "../../output/playwright/test-results",
  fullyParallel: false,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  workers: 1,
  timeout: 4 * 60_000,
  expect: { timeout: 15_000 },
  reporter: process.env.CI
    ? [["github"], ["html", { outputFolder: "../../output/playwright/report", open: "never" }]]
    : [["list"], ["html", { outputFolder: "../../output/playwright/report", open: "never" }]],
  use: {
    ...devices["Desktop Chrome"],
    trace: "retain-on-failure",
    screenshot: "only-on-failure",
    video: "retain-on-failure",
  },
  webServer: [
    {
      command: "pnpm --filter customer-web exec next start -p 3000",
      url: "http://127.0.0.1:3000",
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
      env: {
        ...authEnv("customer"),
        ORGANIZER_ZONE_URL: "http://localhost:3001",
        SCANNER_ZONE_URL: "http://localhost:3002",
      },
    },
    {
      command: "pnpm --filter organizer-dashboard exec next start -p 3001",
      url: "http://127.0.0.1:3001/organizer",
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
      env: authEnv("organizer"),
    },
    {
      command: "pnpm --filter gate-scanner exec next start -p 3002",
      url: "http://127.0.0.1:3002/scanner",
      reuseExistingServer: !process.env.CI,
      timeout: 120_000,
      env: authEnv("scanner"),
    },
  ],
  projects: [
    {
      name: "frontend-contract",
      testMatch: /frontend-contract\.spec\.ts/,
      use: { baseURL: "http://localhost:3000" },
    },
    {
      name: "full-stack",
      testMatch: /full-stack\.spec\.ts/,
      use: { baseURL: "http://localhost:3000" },
    },
  ],
});
