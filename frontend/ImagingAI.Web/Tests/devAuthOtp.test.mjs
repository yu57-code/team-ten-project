import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import test from "node:test";

const source = await readFile(new URL("../wwwroot/js/devAuth.js", import.meta.url), "utf8");
const api = await import("data:text/javascript;base64," + Buffer.from(source).toString("base64"));

class Form {
    constructor(code) {
        this.fields = Array.from(code, value => ({ value }));
    }
    querySelectorAll() { return this.fields; }
}
globalThis.HTMLFormElement = Form;

test("OTP keeps leading zeros, clears input, and uses fresh CSRF", async () => {
    const form = new Form("012345");
    const requests = [];
    globalThis.fetch = async (path, options) => {
        requests.push({ path, options });
        assert.ok(form.fields.every(field => field.value === ""));
        if (path.endsWith("/csrf")) return Response.json({ requestToken: "phase-specific" });
        return Response.json({ state: "roleSelectionRequired", availableRoleCodes: ["MEDICAL_REVIEWER", "SYSTEM_ADMIN"],
            expiresInSeconds: 120, otp: "012345", email: "synthetic@example.invalid" });
    };
    const result = await api.verifyOtp(form);
    assert.equal(result.status, 200);
    assert.equal(result.json.state, "roleSelectionRequired");
    assert.equal(result.json.otp, undefined);
    assert.equal(result.json.email, undefined);
    assert.deepEqual(requests.map(r => r.path), ["/api/auth/csrf", "/api/auth/otp/verify"]);
    assert.equal(requests[1].options.headers["X-CSRF-TOKEN"], "phase-specific");
    assert.deepEqual(JSON.parse(requests[1].options.body), { code: "012345" });
});

test("incomplete and non-ASCII OTPs never reach the network", async () => {
    globalThis.fetch = () => assert.fail("network called for invalid input");
    for (const code of ["01234", "０12345", "0123456"]) {
        const form = new Form(code);
        assert.equal((await api.verifyOtp(form)).status, 400);
        assert.ok(form.fields.every(field => field.value === ""));
    }
});

test("resend clears stale input, has no payload, honors server retry header and does not retry", async () => {
    const form = new Form("123456");
    const paths = [];
    globalThis.fetch = async (path, options) => {
        paths.push(path);
        if (path.endsWith("/csrf")) return Response.json({ requestToken: "otp" });
        assert.equal(options.body, undefined);
        return Response.json({ code: "rate_limited" }, { status: 429, headers: { "Retry-After": "42" } });
    };
    const result = await api.resendOtp(form);
    assert.equal(result.status, 429);
    assert.equal(result.retryAfter, "42");
    assert.deepEqual(paths, ["/api/auth/csrf", "/api/auth/otp/resend"]);
    assert.ok(form.fields.every(field => field.value === ""));
});

test("a second submit or resend cannot share the in-flight form", async () => {
    const form = new Form("123456");
    let release;
    globalThis.fetch = () => new Promise(resolve => { release = resolve; });
    const first = api.verifyOtp(form);
    assert.equal((await api.verifyOtp(form)).transportError, "protocol_error");
    assert.equal((await api.resendOtp(form)).transportError, "protocol_error");
    release(Response.json({ code: "verification_unavailable" }, { status: 503 }));
    assert.equal((await first).duringCsrf, true);
});

test("recovery does not cancel a session established in another tab", async () => {
    const requests = [];
    globalThis.fetch = async (path, options) => {
        requests.push({ path, options });
        if (path.endsWith("/csrf")) return Response.json({ requestToken: "previous-phase" });
        if (path.endsWith("/me")) return Response.json({ code: "invalid_session" }, { status: 401 });
        return Response.json({ code: "invalid_csrf" }, { status: 400 });
    };
    assert.equal((await api.recoverLogin()).status, 400);
    assert.deepEqual(requests.map(r => r.path), ["/api/auth/csrf", "/api/auth/me", "/api/auth/logout"]);
    assert.equal(requests[2].options.headers["X-CSRF-TOKEN"], "previous-phase");
});
