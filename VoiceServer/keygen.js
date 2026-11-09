// Makes voice chat dev keys. A key is "<name>.<signature>", signed with KEY_SECRET, so the server can check any
// key without a list of them, and new keys work straight away without touching the server.
//
//   node keygen.js --new-secret                     prints a new secret, for KEY_SECRET on the server
//   node keygen.js --secret-file secret.txt alex    prints alex's key (or set KEY_SECRET instead of --secret-file)
//
// To stop a key working, add its name to REVOKED on the server.

"use strict";

const crypto = require("crypto");
const fs = require("fs");

const NAME_PATTERN = /^[A-Za-z0-9_-]{1,32}$/;

function sign(secret, name) {
    return crypto.createHmac("sha256", secret).update(`fluxstrap-voice:${name}`).digest("base64url").slice(0, 32);
}

function makeKey(secret, name) {
    if (!NAME_PATTERN.test(name)) throw new Error("names can only use letters, numbers, - and _ (up to 32)");
    return `${name}.${sign(secret, name)}`;
}

// returns the key's name if it was made with this secret, otherwise null
function checkKey(secret, key) {
    if (!secret || typeof key !== "string") return null;

    const dot = key.lastIndexOf(".");
    if (dot <= 0) return null;

    const name = key.slice(0, dot);
    if (!NAME_PATTERN.test(name)) return null;

    const given = Buffer.from(key.slice(dot + 1));
    const expected = Buffer.from(sign(secret, name));

    return given.length === expected.length && crypto.timingSafeEqual(given, expected) ? name : null;
}

module.exports = { makeKey, checkKey, NAME_PATTERN };

if (require.main === module) {
    const args = process.argv.slice(2);

    if (args[0] === "--new-secret") {
        console.log(crypto.randomBytes(32).toString("base64url"));
        process.exit(0);
    }

    let secret = process.env.KEY_SECRET || "";
    const fileIndex = args.indexOf("--secret-file");

    if (fileIndex !== -1) {
        secret = fs.readFileSync(args[fileIndex + 1], "utf8").trim();
        args.splice(fileIndex, 2);
    }

    if (secret.length < 32) {
        console.error("No secret: pass --secret-file or set KEY_SECRET (make one with --new-secret)");
        process.exit(1);
    }

    try {
        console.log(makeKey(secret, (args[0] || "").trim()));
    } catch (error) {
        console.error(error.message);
        process.exit(1);
    }
}
