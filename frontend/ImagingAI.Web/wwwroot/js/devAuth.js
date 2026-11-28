const prefix = "/api/auth/";

if (typeof window !== "undefined") {
    window.addEventListener("pageshow", event => {
        if (event.persisted && (location.pathname === "/login" || location.pathname === "/")) {
            location.reload();
        }
    });
}

async function request(endpoint, method = "GET", payload, csrfToken) {
    const headers = { Accept: "application/json, application/problem+json" };
    if (payload !== undefined) {
        headers["Content-Type"] = "application/json";
    }
    if (csrfToken !== undefined) {
        headers["X-CSRF-TOKEN"] = csrfToken;
    }

    try {
        const response = await fetch(prefix + endpoint, {
            method,
            headers,
            credentials: "same-origin",
            mode: "same-origin",
            cache: "no-store",
            redirect: "manual",
            body: payload === undefined ? undefined : JSON.stringify(payload)
        });
        if (response.type === "opaqueredirect" || response.redirected ||
            (response.status >= 300 && response.status < 400)) {
            return { transportError: "protocol_error" };
        }

        const result = { status: response.status, retryAfter: response.headers.get("Retry-After") };
        if (response.status === 204) {
            return result;
        }

        const mediaType = response.headers.get("Content-Type")?.split(";")[0].trim().toLowerCase();
        if (mediaType === "application/json" || mediaType?.endsWith("+json")) {
            const text = await response.text();
            try {
                result.json = JSON.parse(text);
            } catch (error) {
                if (!(error instanceof SyntaxError)) throw error;
                // Keep HTTP status for non-JSON failures. Successful calls still require valid JSON.
            }
        }
        return result;
    } catch {
        return { transportError: "network_error" };
    }
}

async function post(endpoint, payload) {
    const csrf = await getCsrf();
    if (csrf.transportError || csrf.status !== 200) {
        return { ...csrf, duringCsrf: true };
    }

    const token = csrf.json?.requestToken;
    if (typeof token !== "string" || !token.trim()) {
        return { status: csrf.status, transportError: "protocol_error", duringCsrf: true };
    }

    return request(endpoint, "POST", payload, token);
}

export function getCsrf() {
    return request("csrf");
}

export function login(username, password) {
    if (password === undefined && typeof username !== "string") {
        return loginWithForm(username);
    }
    return post("login", { username, password });
}

const submittingForms = new WeakSet();

async function loginWithForm(form) {
    if (!(form instanceof HTMLFormElement) || submittingForms.has(form)) {
        return { transportError: "protocol_error" };
    }

    const usernameField = form.querySelector("input#acc");
    const passwordField = form.querySelector("input#pwd");
    if (!(passwordField instanceof HTMLInputElement)) {
        return { transportError: "protocol_error" };
    }

    submittingForms.add(form);
    let credentials;
    try {
        credentials = { username: "", password: passwordField.value };
        passwordField.value = "";
        if (!(usernameField instanceof HTMLInputElement)) {
            return { transportError: "protocol_error" };
        }
        credentials.username = usernameField.value;
        if (!credentials.username.trim() || !credentials.password) {
            return { status: 400, json: { code: "invalid_request" } };
        }
        return publicAuthResult(await post("login", credentials));
    } finally {
        if (credentials) credentials.password = "";
        submittingForms.delete(form);
    }
}

function publicAuthResult(result) {
    // Project the public contract before crossing back into the server circuit.
    const { status, retryAfter, transportError, duringCsrf, json } = result;
    const publicResult = { status, retryAfter, transportError, duringCsrf };
    if (json && typeof json === "object" && !Array.isArray(json)) {
        const { state, code, user, availableRoleCodes, expiresInSeconds, resendAfterSeconds } = json;
        publicResult.json = duringCsrf ? { code } : { state, code, availableRoleCodes, expiresInSeconds, resendAfterSeconds };
        if (!duringCsrf && user != null) {
            const { userId, username, activeRoleCode } = user;
            publicResult.json.user = { userId, username, activeRoleCode };
        }
    }
    return publicResult;
}

const preparedOtpForms = new WeakSet();
const otpFields = form => Array.from(form.querySelectorAll("input[data-otp-digit]"));

export function prepareOtp(form) {
    if (!(form instanceof HTMLFormElement) || preparedOtpForms.has(form)) return;
    preparedOtpForms.add(form);
    const fields = otpFields(form);
    const distribute = (field, text) => {
        const start = fields.indexOf(field);
        if (start < 0) return;
        if (!/^[0-9]*$/.test(text)) {
            field.value = "";
            return;
        }
        const digits = text.slice(0, fields.length - start);
        field.value = "";
        for (let i = 0; i < digits.length; i++) fields[start + i].value = digits[i];
        if (digits.length) fields[Math.min(start + digits.length, fields.length - 1)].focus();
    };
    form.addEventListener("input", event => distribute(event.target, event.target.value));
    form.addEventListener("paste", event => {
        if (!fields.includes(event.target)) return;
        event.preventDefault();
        distribute(event.target, event.clipboardData.getData("text").trim());
    });
    form.addEventListener("keydown", event => {
        const index = fields.indexOf(event.target);
        if (event.key === "Backspace" && index > 0 && !fields[index].value) fields[index - 1].focus();
    });
    fields[0]?.focus();
}

async function submitOtp(form, verify) {
    if (!(form instanceof HTMLFormElement) || submittingForms.has(form)) {
        return { transportError: "protocol_error" };
    }
    submittingForms.add(form);
    const fields = otpFields(form);
    const valid = fields.length === 6 && fields.every(field => /^[0-9]$/.test(field.value));
    const payload = verify && valid ? { code: fields.map(field => field.value).join("") } : undefined;
    fields.forEach(field => { field.value = ""; });
    try {
        if (verify && !payload) return { status: 400, json: { code: "invalid_request" } };
        return publicAuthResult(await post(verify ? "otp/verify" : "otp/resend", payload));
    } finally {
        if (payload) payload.code = "";
        submittingForms.delete(form);
    }
}

export function verifyOtp(form) {
    return submitOtp(form, true);
}

export function resendOtp(form) {
    return submitOtp(form, false);
}

export function selectRole(roleCode) {
    return post("select-role", { roleCode });
}

export function me() {
    return request("me");
}

export async function recoverLogin() {
    const csrf = await getCsrf();
    const token = csrf.json?.requestToken;
    if (csrf.transportError || csrf.status !== 200) return { ...csrf, duringCsrf: true };
    if (typeof token !== "string" || !token.trim()) return { transportError: "protocol_error" };
    const identity = await me();
    // Bind cancellation to the phase seen before checking identity. A different
    // tab completing login in between must not turn this into a session logout.
    return identity.status === 401 ? request("logout", "POST", undefined, token) : identity;
}

export function logout() {
    return post("logout");
}
