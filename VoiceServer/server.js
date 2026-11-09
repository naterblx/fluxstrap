// FluxStrap voice server
//
// Relays voice chat between FluxStrap users. Everyone in the same Fluxer game server (same place and job id)
// shares a room. Anyone can connect and listen; only connections that present a dev key can send audio.
//
//   KEY_SECRET the secret dev keys are signed with (see keygen.js), so any key made with it can talk
//   REVOKED    comma separated names whose keys no longer work, e.g. "alex,sam"
//   DEV_KEYS   optional extra name=key pairs that work without the secret, e.g. "nate=4f0c..."
//   PORT       port to listen on (set automatically on Render)
//
// Protocol (WebSocket at /voice):
//   client -> server  text   {"t":"hello","v":1,"room":"<placeId>/<jobId>","user":<userId>,"key":"<dev key or empty>"}
//   server -> client  text   {"t":"welcome","id":<n>,"speaker":<bool>,"name":<string|null>}
//   server -> client  text   {"t":"room","listeners":<n>,"speakers":[{"id":<n>,"name":"<name>"}]}
//   speaker -> server text   {"t":"talk","on":<bool>}                 -> relayed as {"t":"talk","id":<n>,"on":<bool>}
//   speaker -> server binary one Opus frame (48 kHz mono, 20 ms)     -> relayed as [uint32 LE speaker id][frame]

"use strict";

const crypto = require("crypto");
const http = require("http");
const { WebSocketServer } = require("ws");
const { checkKey } = require("./keygen");

const PORT = Number(process.env.PORT) || 8080;
const MAX_CLIENTS = 2000;
const MAX_ROOM_SIZE = 200;
const MAX_FRAME_BYTES = 1275; // largest opus packet
const MAX_FRAMES_PER_SECOND = 75; // 50 is normal for 20 ms frames
const HELLO_TIMEOUT_MS = 10000;
const PING_INTERVAL_MS = 20000;
const ROOM_PATTERN = /^\d{1,20}\/[\w-]{1,64}$/;

const KEY_SECRET = (process.env.KEY_SECRET || "").trim();
const revoked = new Set((process.env.REVOKED || "").split(",").map(x => x.trim().toLowerCase()).filter(Boolean));
const devKeys = new Map(); // sha256(key) -> name

for (const pair of (process.env.DEV_KEYS || "").split(",")) {
    const index = pair.indexOf("=");
    if (index <= 0) continue;

    const name = pair.slice(0, index).trim();
    const key = pair.slice(index + 1).trim();

    if (name && key.length >= 16) devKeys.set(hash(key), name);
}

const rooms = new Map(); // room -> Set of clients
let nextId = 1;

function hash(value) {
    return crypto.createHash("sha256").update(value).digest();
}

function lookupKey(key) {
    if (typeof key !== "string" || key.length < 16 || key.length > 200) return null;

    let name = checkKey(KEY_SECRET, key);

    if (name === null) {
        const digest = hash(key);

        for (const [known, listed] of devKeys) {
            if (crypto.timingSafeEqual(known, digest)) name = listed;
        }
    }

    return name !== null && !revoked.has(name.toLowerCase()) ? name : null;
}

function send(client, message) {
    if (client.socket.readyState === client.socket.OPEN) client.socket.send(JSON.stringify(message));
}

function broadcastRoom(roomName) {
    const members = rooms.get(roomName);
    if (!members) return;

    const speakers = [...members].filter(x => x.speaker).map(x => ({ id: x.id, name: x.name }));
    const message = { t: "room", listeners: members.size, speakers };

    for (const member of members) send(member, message);
}

function leave(client) {
    if (!client.room) return;

    const members = rooms.get(client.room);

    if (members) {
        members.delete(client);

        if (members.size === 0) rooms.delete(client.room);
        else broadcastRoom(client.room);
    }

    client.room = null;
}

function onHello(client, message) {
    if (message.v !== 1 || typeof message.room !== "string" || !ROOM_PATTERN.test(message.room)) {
        send(client, { t: "error", message: "bad hello" });
        client.socket.close(4000, "bad hello");
        return;
    }

    const members = rooms.get(message.room) || new Set();

    if (members.size >= MAX_ROOM_SIZE) {
        send(client, { t: "error", message: "room full" });
        client.socket.close(4001, "room full");
        return;
    }

    const name = lookupKey(message.key);

    client.room = message.room;
    client.speaker = name !== null;
    client.name = name;
    client.user = Number.isSafeInteger(message.user) ? message.user : 0;

    members.add(client);
    rooms.set(client.room, members);

    send(client, { t: "welcome", id: client.id, speaker: client.speaker, name: client.name });
    broadcastRoom(client.room);

    console.log(`${client.speaker ? `speaker ${client.name}` : "listener"} joined ${client.room} (${members.size} in room)`);
}

function relayAudio(client, data) {
    const now = Date.now();

    if (now - client.windowStart >= 1000) {
        client.windowStart = now;
        client.frames = 0;
    }

    if (++client.frames > MAX_FRAMES_PER_SECOND || data.length === 0 || data.length > MAX_FRAME_BYTES) return;

    const packet = Buffer.allocUnsafe(4 + data.length);
    packet.writeUInt32LE(client.id, 0);
    data.copy(packet, 4);

    for (const member of rooms.get(client.room) || []) {
        if (member !== client && member.socket.readyState === member.socket.OPEN) member.socket.send(packet);
    }
}

const server = http.createServer((request, response) => {
    if (request.url === "/healthz") {
        response.writeHead(200, { "Content-Type": "text/plain" });
        response.end("ok");
        return;
    }

    response.writeHead(200, { "Content-Type": "text/plain" });
    response.end("FluxStrap voice server\n");
});

const wss = new WebSocketServer({ server, path: "/voice", maxPayload: 4096 });

wss.on("connection", socket => {
    if (wss.clients.size > MAX_CLIENTS) {
        socket.close(1013, "server full");
        return;
    }

    const client = { id: nextId++, socket, room: null, speaker: false, name: null, user: 0, alive: true, windowStart: 0, frames: 0 };
    const helloTimer = setTimeout(() => { if (!client.room) socket.close(4002, "no hello"); }, HELLO_TIMEOUT_MS);

    socket.on("pong", () => { client.alive = true; });

    socket.on("message", (data, isBinary) => {
        if (isBinary) {
            // listeners can't talk, whatever their client sends
            if (client.room && client.speaker) relayAudio(client, data);
            return;
        }

        let message;

        try {
            message = JSON.parse(data.toString());
        } catch {
            return;
        }

        if (message.t === "hello" && !client.room) {
            onHello(client, message);
        } else if (message.t === "talk" && client.room && client.speaker) {
            const relay = JSON.stringify({ t: "talk", id: client.id, on: message.on === true });

            for (const member of rooms.get(client.room) || []) {
                if (member !== client && member.socket.readyState === member.socket.OPEN) member.socket.send(relay);
            }
        }
    });

    socket.on("close", () => {
        clearTimeout(helloTimer);
        leave(client);
    });

    socket.on("error", () => {});

    client.pingTimer = setInterval(() => {
        if (!client.alive) {
            socket.terminate();
            return;
        }

        client.alive = false;
        socket.ping();
    }, PING_INTERVAL_MS);

    socket.on("close", () => clearInterval(client.pingTimer));
});

server.listen(PORT, () => console.log(`FluxStrap voice server listening on ${PORT}, signed keys ${KEY_SECRET.length >= 32 ? "on" : "off (no KEY_SECRET)"}, ${devKeys.size} listed key(s), ${revoked.size} revoked`));
