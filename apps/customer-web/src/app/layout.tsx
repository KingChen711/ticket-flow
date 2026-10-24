import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
import Link from "next/link";
import { auth, signIn, signOut } from "@/auth";
import "./globals.css";

const geistSans = Geist({
  variable: "--font-geist-sans",
  subsets: ["latin"],
});

const geistMono = Geist_Mono({
  variable: "--font-geist-mono",
  subsets: ["latin"],
});

export const metadata: Metadata = {
  title: "TicketFlow",
  description: "Find and buy tickets for live events",
};

export default async function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  const session = await auth();

  return (
    <html
      lang="en"
      className={`${geistSans.variable} ${geistMono.variable} h-full antialiased`}
    >
      <body className="flex min-h-full flex-col">
        <a href="#main" className="sr-only focus:not-sr-only focus:fixed focus:left-4 focus:top-4 focus:z-50 focus:rounded-lg focus:bg-surface focus:px-4 focus:py-2">
          Skip to content
        </a>
        <header className="sticky top-0 z-40 border-b border-border bg-surface/90 backdrop-blur">
          <div className="mx-auto flex w-full max-w-5xl items-center justify-between gap-4 px-4 py-3 sm:px-6">
            <Link href="/" className="flex items-center gap-2 text-lg font-bold tracking-tight">
              <TicketIcon />
              TicketFlow
            </Link>
            {session && !session.error ? (
              <nav aria-label="Account" className="flex items-center gap-1 text-sm sm:gap-3">
                <Link href="/orders" className="btn-ghost">
                  My orders
                </Link>
                <span className="hidden max-w-48 truncate text-muted sm:inline">{session.user?.email}</span>
                <form
                  action={async () => {
                    "use server";
                    await signOut();
                  }}
                >
                  <button type="submit" className="btn-secondary">
                    Sign out
                  </button>
                </form>
              </nav>
            ) : (
              <form
                action={async () => {
                  "use server";
                  await signIn("keycloak");
                }}
              >
                <button type="submit" className="btn-primary">
                  {session?.error ? "Session expired — sign in again" : "Sign in"}
                </button>
              </form>
            )}
          </div>
        </header>
        <main id="main" className="flex-1">{children}</main>
        <footer className="border-t border-border py-6 text-center text-xs text-muted">
          TicketFlow · demo project, payments are simulated
        </footer>
      </body>
    </html>
  );
}

function TicketIcon() {
  return (
    <svg aria-hidden="true" viewBox="0 0 24 24" className="size-6 text-primary" fill="none" stroke="currentColor" strokeWidth={2} strokeLinecap="round" strokeLinejoin="round">
      <path d="M3 9a3 3 0 0 0 0 6v3a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-3a3 3 0 0 1 0-6V6a2 2 0 0 0-2-2H5a2 2 0 0 0-2 2Z" />
      <path d="M13 5v2M13 11v2M13 17v2" />
    </svg>
  );
}
