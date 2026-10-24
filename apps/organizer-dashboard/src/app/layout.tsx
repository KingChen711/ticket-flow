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
  title: "TicketFlow — Organizer",
  description: "Create and manage events, track revenue",
};

export default async function RootLayout({
  children,
}: Readonly<{
  children: React.ReactNode;
}>) {
  const session = await auth();
  const isOrganizer = !!session?.roles?.includes("organizer") && !session.error;

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
            <Link href="/organizer" className="text-lg font-bold tracking-tight">
              TicketFlow — Organizer
            </Link>
            {session && !session.error ? (
              <nav aria-label="Account" className="flex items-center gap-1 text-sm sm:gap-3">
                {isOrganizer && (
                  <Link href="/organizer/events/new" className="btn-primary">
                    New event
                  </Link>
                )}
                <span className="hidden max-w-48 truncate text-muted sm:inline">{session.user?.email}</span>
                <form
                  action={async () => {
                    "use server";
                    await signOut({ redirectTo: "/organizer" });
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
                  // NextAuth's default post-login redirect is the site root ("/") — 404s now that
                  // this app's real content lives under /organizer, not "/" (Multi-Zones restructure,
                  // docs/phase-8-plan.md Commit 9).
                  await signIn("keycloak", { redirectTo: "/organizer" });
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
      </body>
    </html>
  );
}
