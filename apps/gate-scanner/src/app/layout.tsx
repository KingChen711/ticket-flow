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
  manifest: "/manifest.json",
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
      <body className="min-h-full flex flex-col">
        <header className="flex items-center justify-between border-b px-6 py-4">
          <span className="text-lg font-semibold">TicketFlow — Gate Scanner</span>
          {session && !session.error ? (
            <div className="flex items-center gap-4 text-sm">
              <span>{session.user?.email}</span>
              <form
                action={async () => {
                  "use server";
                  await signOut({ redirectTo: "/scanner" });
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
                // See organizer-dashboard/src/app/layout.tsx for why redirectTo is needed here.
                await signIn("keycloak", { redirectTo: "/scanner" });
              }}
            >
              <button type="submit" className="text-blue-600 underline">
                {session?.error ? "Session expired — sign in again" : "Sign in"}
              </button>
            </form>
          )}
        </header>
        <main className="flex-1">{children}</main>
      </body>
    </html>
  );
}
