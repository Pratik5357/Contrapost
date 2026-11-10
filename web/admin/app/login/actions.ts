"use server";

import { redirect } from "next/navigation";
import { checkCredentials, endSession, guestModeEnabled, startSession } from "@/lib/session";

export async function logIn(form: FormData): Promise<void> {
  const username = String(form.get("username") ?? "");
  const password = String(form.get("password") ?? "");
  if (!checkCredentials(username, password)) {
    redirect("/login?error=1");
  }
  await startSession(username, false);
  redirect("/");
}

export async function continueAsGuest(): Promise<void> {
  if (!guestModeEnabled()) redirect("/login");
  await startSession("guest", true);
  redirect("/");
}

export async function logOut(): Promise<void> {
  await endSession();
  redirect("/login");
}
