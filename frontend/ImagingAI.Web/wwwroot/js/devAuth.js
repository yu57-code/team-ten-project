const prefix = "/api/auth/";

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
        const result = await post("login", credentials);
        // Project the public contract before crossing back into the server circuit.
        const { status, retryAfter, transportError, duringCsrf, json } = result;
        const publicResult = { status, retryAfter, transportError, duringCsrf };
        if (json && typeof json === "object" && !Array.isArray(json)) {
            const { state, code, user, availableRoleCodes, expiresInSeconds } = json;
            publicResult.json = duringCsrf ? { code } : { state, code, availableRoleCodes, expiresInSeconds };
            if (!duringCsrf && user != null) {
                const { userId, username, activeRoleCode } = user;
                publicResult.json.user = { userId, username, activeRoleCode };
            }
        }
        return publicResult;
    } finally {
        if (credentials) credentials.password = "";
        submittingForms.delete(form);
    }
}

export function selectRole(roleCode) {
    return post("select-role", { roleCode });
}

export function me() {
    return request("me");
}

export function logout() {
    return post("logout");
}
