import { auth, signIn, signOut } from "@/auth";

// Phase 8 Commit 3 probe page — proves the Auth.js + Keycloak PKCE chain end-to-end before any
// real screen gets built. Not the real customer-web home page (Commit 4 replaces this).
export default async function Home() {
  const session = await auth();

  if (!session) {
    return (
      <main className="flex min-h-screen flex-col items-center justify-center gap-4">
        <p>Not signed in.</p>
        <form
          action={async () => {
            "use server";
            await signIn("keycloak");
          }}
        >
          <button type="submit" className="rounded bg-black px-4 py-2 text-white">
            Sign in with Keycloak
          </button>
        </form>
      </main>
    );
  }

  return (
    <main className="flex min-h-screen flex-col items-center justify-center gap-4">
      <p>Signed in as {session.user?.email}</p>
      <p className="max-w-md break-all text-xs text-gray-500">
        accessToken: {session.accessToken?.slice(0, 40)}...
      </p>
      <a href="/probe" className="rounded bg-blue-600 px-4 py-2 text-white">
        Probe gateway calls
      </a>
      <form
        action={async () => {
          "use server";
          await signOut();
        }}
      >
        <button type="submit" className="rounded border px-4 py-2">
          Sign out
        </button>
      </form>
    </main>
  );
}
