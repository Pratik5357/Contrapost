// Prints an ADMIN_PASSWORD_HASH for the given password: npm run hash-password -- '<password>'
import { randomBytes, scryptSync } from "node:crypto";

const password = process.argv[2];
if (!password) {
  console.error("usage: npm run hash-password -- '<password>'");
  process.exit(1);
}
const salt = randomBytes(16);
console.log(`scrypt:${salt.toString("base64url")}:${scryptSync(password, salt, 32).toString("base64url")}`);
