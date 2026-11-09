// Checks the relay rules: dev keys can talk, everyone else in the room only hears, other rooms hear nothing.
// Run with: npm test

"use strict";

const { spawn } = require("child_process");
const WebSocket = require("ws");

const { makeKey } = require("./keygen");

const PORT = 18080 + Math.floor(Math.random() * 1000);
const SECRET = "test-secret-0123456789abcdefghijklmnop";
const KEY = makeKey(SECRET, "nate");
const LISTED_KEY = "listed-key-0123456789abcdef";
const URL = `ws://127.0.0.1:${PORT}/voice`;

let failures = 0;

function check(name, ok) {
    console.log(`${ok ? "PASS" : "FAIL"}  ${name}`);
    if (!ok) failures++;
}

function connect(room, key = "", version = 2) {
    return new Promise((resolve, reject) => {
        const socket = new WebSocket(URL);
        const client = { socket, audio: [], messages: [], welcome: null };

        socket.on("message", (data, isBinary) => {
            if (isBinary) {
                client.audio.push(data);
                return;
            }

            const message = JSON.parse(data.toString());
            client.messages.push(message);

            if (message.t === "welcome") {
                client.welcome = message;
                resolve(client);
            }
        });

        socket.on("open", () => socket.send(JSON.stringify({ t: "hello", v: version, room, user: 1, key })));
        socket.on("error", reject);
    });
}

const wait = ms => new Promise(resolve => setTimeout(resolve, ms));

(async () => {
    const env = { ...process.env, PORT, KEY_SECRET: SECRET, REVOKED: "Sam", DEV_KEYS: `legacy=${LISTED_KEY}` };
    const server = spawn(process.execPath, [__dirname + "/server.js"], { env, stdio: "inherit" });
    await wait(700);

    try {
        // these go in a room of their own so they don't change the counts below
        const keyRoom = "6602/key-checks";
        const revoked = await connect(keyRoom, makeKey(SECRET, "sam"));
        const forged = await connect(keyRoom, "nate.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA");
        const otherSecret = await connect(keyRoom, makeKey("some-other-secret-0123456789abcdefgh", "nate"));
        const listed = await connect(keyRoom, LISTED_KEY);

        check("revoked names can't talk", revoked.welcome.speaker === false);
        check("forged signatures can't talk", forged.welcome.speaker === false);
        check("keys from another secret can't talk", otherSecret.welcome.speaker === false);
        check("DEV_KEYS keys still work", listed.welcome.speaker === true && listed.welcome.name === "legacy");

        for (const client of [revoked, forged, otherSecret, listed]) client.socket.close();

        const speaker = await connect("6602/abc-123", KEY);
        const listener = await connect("6602/abc-123");
        const faker = await connect("6602/abc-123", "not-a-real-key-at-all");
        const otherRoom = await connect("6602/other-server");
        const far = await connect("6602/abc-123");
        const nowhere = await connect("6602/abc-123");
        const oldClient = await connect("6602/abc-123", "", 1);

        const moveTo = (client, x, y, z) => client.socket.send(JSON.stringify({ t: "pos", x, y, z }));
        moveTo(speaker, 10, 5, 10);
        moveTo(listener, 30, 5, 10);    // 20 studs away
        moveTo(faker, 12, 5, 10);
        moveTo(otherRoom, 10, 5, 10);
        moveTo(far, 10, 5, 200);        // 190 studs away
        // nowhere never says where it is, oldClient is FluxStrap 1.2.x

        check("signed key is a speaker", speaker.welcome.speaker === true && speaker.welcome.name === "nate");
        check("no key is a listener", listener.welcome.speaker === false);
        check("wrong key is a listener", faker.welcome.speaker === false);

        await wait(200);
        const room = listener.messages.filter(x => x.t === "room").pop();
        check("room lists the speaker", room && room.listeners === 6 && room.speakers.length === 1 && room.speakers[0].name === "nate");

        const frame = Buffer.from([1, 2, 3, 4, 5]);
        speaker.socket.send(frame);
        faker.socket.send(Buffer.from([9, 9, 9]));
        speaker.socket.send(JSON.stringify({ t: "talk", on: true }));
        await wait(300);

        const heard = listener.audio[0];
        check("listener hears the speaker", listener.audio.length === 1 && heard.readUInt32LE(0) === speaker.welcome.id && heard.subarray(16).equals(frame));
        check("audio carries the speaker's position", heard && heard.readFloatLE(4) === 10 && heard.readFloatLE(8) === 5 && heard.readFloatLE(12) === 10);
        check("people far away don't hear the speaker", far.audio.length === 0);
        check("people with no position don't hear the speaker", nowhere.audio.length === 0);
        check("1.2.x clients don't get audio they can't read", oldClient.audio.length === 0);

        // walk over to the far listener, who now hears, and the first one doesn't
        moveTo(speaker, 10, 5, 190);
        await wait(200);
        speaker.socket.send(frame);
        await wait(300);
        check("walking up to someone lets them hear you", far.audio.length === 1 && listener.audio.length === 1);
        check("faker's audio is dropped", speaker.audio.length === 0 && listener.audio.length === 1);
        check("other rooms hear nothing", otherRoom.audio.length === 0);
        check("talk indicator is relayed", listener.messages.some(x => x.t === "talk" && x.id === speaker.welcome.id && x.on === true));

        const badRoom = await new Promise(resolve => {
            const socket = new WebSocket(URL);
            socket.on("open", () => socket.send(JSON.stringify({ t: "hello", v: 1, room: "../etc", user: 1 })));
            socket.on("close", code => resolve(code));
        });
        check("bad room names are refused", badRoom === 4000);

        for (const client of [speaker, listener, faker, otherRoom, far, nowhere, oldClient]) client.socket.close();
    } catch (error) {
        console.error(error);
        failures++;
    } finally {
        server.kill();
    }

    console.log(failures === 0 ? "all passed" : `${failures} failed`);
    process.exit(failures === 0 ? 0 : 1);
})();
