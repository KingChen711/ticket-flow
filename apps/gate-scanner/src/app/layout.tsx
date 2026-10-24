import type { Metadata } from "next";
import { Geist, Geist_Mono } from "next/font/google";
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
  title: "TicketFlow — Gate Scanner",
  description: "Scan ticket QR codes to check attendees in at the gate",
  manifest: "/scanner/manifest.webmanifest",
};

export const viewport = {
  themeColor: "#000000",
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
        <header className="sticky top-0 z-40 border-b border-border bg-surface/90 backdrop-blur">
          <div className="mx-auto flex w-full max-w-2xl items-center justify-between gap-4 px-4 py-3">
            <span className="text-lg font-bold tracking-tight">TicketFlow — Gate Scanner</span>
            {session && !session.error ? (
              <div className="flex items-center gap-3 text-sm">
                <span className="hidden max-w-40 truncate text-muted sm:inline">{session.user?.email}</span>
                <form
                  action={async () => {
                    "use server";
                    await signOut({ redirectTo: "/scanner/" });
                  }}
                >
                  <button type="submit" className="btn-secondary">
                    Sign out
                  </button>
                </form>
              </div>
            ) : (
              <form
                action={async () => {
                  "use server";
                  // See organizer-dashboard/src/app/layout.tsx for why redirectTo is needed here.
                  await signIn("keycloak", { redirectTo: "/scanner/" });
                }}
              >
                <button type="submit" className="btn-primary">
                  {session?.error ? "Session expired — sign in again" : "Sign in"}
                </button>
              </form>
            )}
          </div>
        </header>
        <main className="flex-1">{children}</main>
      </body>
    </html>
  );
}
