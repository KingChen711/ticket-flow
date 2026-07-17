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
  const isOrganizer = !!session?.roles?.includes("organizer");

  return (
    <html
      lang="en"
      className={`${geistSans.variable} ${geistMono.variable} h-full antialiased`}
    >
      <body className="min-h-full flex flex-col">
        <header className="flex items-center justify-between border-b px-6 py-4">
          <Link href="/" className="text-lg font-semibold">
            TicketFlow — Organizer
          </Link>
          {session ? (
            <div className="flex items-center gap-4 text-sm">
              {isOrganizer && (
                <Link href="/events/new" className="text-blue-600 underline">
                  New event
                </Link>
              )}
              <span>{session.user?.email}</span>
              <form
                action={async () => {
                  "use server";
                  await signOut();
                }}
              >
                <button type="submit" className="text-blue-600 underline">
                  Sign out
                </button>
              </form>
            </div>
          ) : (
            <form
              action={async () => {
                "use server";
                await signIn("keycloak");
              }}
            >
              <button type="submit" className="text-blue-600 underline">
                Sign in
              </button>
            </form>
          )}
        </header>
        <main className="flex-1">{children}</main>
      </body>
    </html>
  );
}
