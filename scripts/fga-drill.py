#!/usr/bin/env python3
"""The scenario of scripts/fga-drill.sh (ADR 0057): department rights through a real OpenFGA, a real DirectoryService and a real EmployeeService.

Departments are made and moved in DirectoryService (the stack's own); its events reach EmployeeService, which keeps OpenFGA's tree in step. Two
non-administrator accounts (editors) are made, one is given a department to manage, and then what each may hire into is asked of the real endpoint,
while the tree changes and while OpenFGA is stopped and started again.
"""
import base64, hashlib, http.cookiejar, json, os, re, statistics, subprocess, sys, time, urllib.error, urllib.parse, urllib.request, uuid

NGINX = "http://localhost"
EMPLOYEE = f"http://localhost:{os.environ.get('EMPLOYEE_PORT', '5361')}"
ADMIN_EMAIL = os.environ.get("ADMIN_EMAIL", "admin@portfolio.local")
# The seeded administrator's password: the repository's committed development default, unless the stack was started with another (the nightly
# run does: the breached-password check refuses the default on a fresh database, and the administrator is then never created).
ADMIN_PASSWORD = os.environ.get("ADMIN_PASSWORD", "ChangeMe123!")
VERIFIER = "k6-load-test-code-verifier-string-43-chars-min-xxxx"
REDIRECT = "http://localhost:3000/auth/callback"
NET = os.environ.get("STACK_NET", "dsporfolio_default")


class NoRedirect(urllib.request.HTTPRedirectHandler):
    def redirect_request(self, *a, **k):
        return None


def opener():
    return urllib.request.build_opener(urllib.request.HTTPCookieProcessor(http.cookiejar.CookieJar()), NoRedirect)


def call(op, method, url, body=None, headers=None, form=False):
    h = dict(headers or {})
    data = None
    if body is not None:
        if form:
            data = urllib.parse.urlencode(body).encode()
            h["Content-Type"] = "application/x-www-form-urlencoded"
        else:
            data = json.dumps(body).encode()
            h["Content-Type"] = "application/json"
    request = urllib.request.Request(url, data=data, headers=h, method=method)
    started = time.time()
    try:
        r = op.open(request)
        status, hdrs, text = r.status, dict(r.headers), r.read().decode()
    except urllib.error.HTTPError as e:
        status, hdrs, text = e.code, dict(e.headers), e.read().decode()
    ms = (time.time() - started) * 1000
    try:
        parsed = json.loads(text) if text else None
    except ValueError:
        parsed = text
    return status, parsed, hdrs, ms


def claims(token):
    part = token.split(".")[1]
    part += "=" * (-len(part) % 4)
    return json.loads(base64.urlsafe_b64decode(part))


_last_auth_post = [0.0]


def pace():
    """Both the edge (ADR 0039) and AuthService itself allow about five POSTs a minute to /auth and /connect, counted in fixed windows; three a minute stays clear of both."""
    wait = 21 - (time.time() - _last_auth_post[0])
    if wait > 0:
        time.sleep(wait)
    _last_auth_post[0] = time.time()


def sign_in(email, password):
    op = opener()
    pace()
    status, body, _, _ = call(op, "POST", f"{NGINX}/auth/login", {"email": email, "password": password})
    assert status == 204, ("login", email, status, body)
    challenge = base64.urlsafe_b64encode(hashlib.sha256(VERIFIER.encode()).digest()).rstrip(b"=").decode()
    query = ("/connect/authorize?client_id=portfolio-frontend&response_type=code&redirect_uri=" + urllib.parse.quote(REDIRECT, safe="")
             + "&scope=openid%20profile%20email%20roles%20offline_access&code_challenge=" + challenge + "&code_challenge_method=S256&state=x")
    _, _, headers, _ = call(op, "GET", NGINX + query)
    code = re.search(r"[?&]code=([^&]+)", headers.get("Location", ""))
    assert code, "no authorization code"
    pace()
    status, tokens, _, _ = call(op, "POST", f"{NGINX}/connect/token", {
        "grant_type": "authorization_code", "code": urllib.parse.unquote(code.group(1)), "redirect_uri": REDIRECT,
        "client_id": "portfolio-frontend", "code_verifier": VERIFIER}, form=True)
    assert status == 200, ("token", status, tokens)
    return op, tokens


def mail_code(to):
    time.sleep(2)
    with urllib.request.urlopen("http://localhost:8025/api/v1/search?query=" + urllib.parse.quote("to:" + to)) as r:
        messages = json.load(r)["messages"]
    return messages


def step_up(op, tokens):
    auth = {"Authorization": "Bearer " + tokens["access_token"]}
    pace()
    requested_at = time.time()
    status, body, _, _ = call(op, "POST", f"{NGINX}/auth/step-up/request-email-code", headers=auth)
    assert status == 204, ("request step-up code", status, body)
    code = None
    for _ in range(20):
        time.sleep(1)
        with urllib.request.urlopen("http://localhost:8025/api/v1/search?query=" + urllib.parse.quote("to:" + ADMIN_EMAIL + " subject:\"Your confirmation code\"")) as r:
            messages = json.load(r)["messages"]
        fresh = [m for m in messages if m["Created"] and time.mktime(time.strptime(m["Created"][:19], "%Y-%m-%dT%H:%M:%S")) - time.timezone >= requested_at - 5]
        if fresh:
            with urllib.request.urlopen(f"http://localhost:8025/api/v1/message/{fresh[0]['ID']}") as r:
                text = json.load(r).get("Text", "")
            found = re.search(r"\b(\d{6})\b", text)
            if found:
                code = found.group(1)
                break
    assert code, "the step-up code never arrived"
    pace()
    status, _, _, _ = call(op, "POST", f"{NGINX}/auth/step-up/verify", {"code": code}, auth)
    assert status == 204, ("step-up verify", status)
    pace()
    status, refreshed, _, _ = call(op, "POST", f"{NGINX}/connect/token", {
        "grant_type": "refresh_token", "refresh_token": tokens["refresh_token"], "client_id": "portfolio-frontend"}, form=True)
    assert status == 200, ("refresh", status)
    assert "elevated_until" in claims(refreshed["access_token"])
    return refreshed["access_token"]


def auth_service(method, path, token, body=None):
    """The admin routes of AuthService are not behind nginx; reach them on the compose network."""
    command = ["docker", "run", "--rm", "--network", NET, "curlimages/curl:latest", "-s", "-w", "\n%{http_code}", "-X", method,
               "-H", "Authorization: Bearer " + token, "-H", "Content-Type: application/json"]
    if body is not None:
        command += ["-d", json.dumps(body)]
    out = subprocess.run(command + ["http://auth_service:5130" + path], capture_output=True, text=True).stdout.rsplit("\n", 1)
    return int(out[1]), out[0]


def register_editor(label):
    email = f"fga-{label}-{uuid.uuid4().hex[:8]}@test.local"
    password = "Zq" + uuid.uuid4().hex + "!9"
    op = opener()
    pace()
    status, body, _, _ = call(op, "POST", f"{NGINX}/auth/register", {"email": email, "password": password})
    assert status == 204, ("register", status, body)
    messages = mail_code(email)
    with urllib.request.urlopen(f"http://localhost:8025/api/v1/message/{messages[0]['ID']}") as r:
        text = json.load(r).get("Text", "")
    link = re.sub(r"^https?://[^/]+", "", re.search(r"https?://\S+", text).group(0).rstrip(".)>"))
    call(op, "GET", NGINX + link)
    return email, password


def say(text=""):
    print(text, flush=True)


def check(name, ok, detail=""):
    say(("PASS  " if ok else "FAIL  ") + name + (f"   {detail}" if detail else ""))
    return ok


def main():
    failures = 0

    say("== accounts: an administrator, and two editors who are not")
    boris_email, boris_password = register_editor("boris")
    carla_email, carla_password = register_editor("carla")
    admin_op, admin_tokens = sign_in(ADMIN_EMAIL, ADMIN_PASSWORD)
    admin_plain = admin_tokens["access_token"]
    admin = step_up(admin_op, admin_tokens)
    ids = {}
    # The account ids come from the admin list.
    for label, email in (("boris", boris_email), ("carla", carla_email)):
        code, text = auth_service("GET", "/admin/accounts?search=" + urllib.parse.quote(email.split("@")[0]), admin)
        ids[label] = json.loads(text)["items"][0]["id"]
        code, _ = auth_service("PUT", f"/admin/accounts/{ids[label]}/roles", admin, {"roles": ["editor"]})
        assert code == 204, ("roles", code)
    boris_op, boris_tokens = sign_in(boris_email, boris_password)
    carla_op, carla_tokens = sign_in(carla_email, carla_password)
    boris = boris_tokens["access_token"]
    carla = carla_tokens["access_token"]
    say(f"boris {ids['boris']}   carla {ids['carla']}   (editors: they may edit, they are not administrators)")

    say("\n== the tree: company > engineering > backend > platform, and sales; a position in each")
    run = uuid.uuid4().hex[:8]
    bearer = lambda t: {"Authorization": "Bearer " + t}
    status, location = call(opener(), "POST", f"{NGINX}/api/locations", {"locationRequest": {
        "name": f"FGA HQ {run}", "address": {"street": f"Main {run}", "city": "Almaty", "country": "Kazakhstan"}, "timezone": "Asia/Almaty"}}, bearer(admin_plain))[:2]
    assert status == 200, ("location", status, location)
    location_id = location["result"]

    def department(name, parent=None):
        request = {"name": {"value": f"{name} {run}"}, "identifier": {"value": (name[:12] + run)[:30]}, "locationsIds": [{"value": location_id}]}
        if parent:
            request["parentDepartmentId"] = {"value": parent}
        s, b, _, _ = call(opener(), "POST", f"{NGINX}/api/departments", {"request": request}, bearer(admin_plain))
        assert s == 200, ("department", name, s, b)
        return b["result"]

    def position(name, department_id):
        s, b, _, _ = call(opener(), "POST", f"{NGINX}/api/positions", {"request": {"name": {"value": f"{name} {run}"}, "departmentIds": [{"value": department_id}]}}, bearer(admin_plain))
        assert s == 200, ("position", name, s, b)
        return b["result"]

    company = department("Company")
    created_at = time.time()
    engineering = department("Engineering", company)
    backend = department("Backend", engineering)
    sales = department("Sales", company)
    platform = department("Platform", backend)
    positions = {d: position("Role", d) for d in (company, engineering, backend, sales, platform)}

    # The sync's own lag: wait for the parent links of the last department to reach the store, observed through a check that needs them.
    status, _, _, _ = call(opener(), "PUT", f"{EMPLOYEE}/api/employees/departments/{engineering}/managers/{ids['boris']}", headers=bearer(admin))
    check("an administrator makes Boris manager of Engineering", status == 204, f"status {status}")

    def hire(token, department_id, label="x"):
        n = uuid.uuid4().hex[:10]
        return call(opener(), "POST", f"{EMPLOYEE}/api/employees", {
            "fullName": f"{label} {n}", "email": f"{label.lower()}-{n}@portfolio.local", "departmentId": department_id, "positionId": positions.get(department_id, str(uuid.uuid4()))},
            {**bearer(token), "Idempotency-Key": str(uuid.uuid4())})

    def eventually_allowed(token, department_id, expect_status, limit=40):
        started = time.time()
        while time.time() - started < limit:
            s, b, _, ms = hire(token, department_id, "probe")
            if s == expect_status:
                return s, time.time() - created_at, ms
            time.sleep(1)
        return s, None, ms

    say("\n== the tree reaches the store through Directory's events; then who may hire where")
    s, lag, _ = eventually_allowed(boris, platform, 200)
    failures += not check("Boris hires into Platform, two levels below the department he manages", s == 200,
                          f"{lag:.1f} s after the departments were made, the hire went through (the events had arrived)" if lag else f"status {s}")
    s, b, _, _ = hire(boris, engineering)
    failures += not check("Boris hires into Engineering itself", s == 200, f"status {s}")
    s, b, _, _ = hire(boris, backend)
    failures += not check("Boris hires into Backend, one level below", s == 200, f"status {s}")
    s, b, _, _ = hire(boris, sales)
    failures += not check("Boris is refused in Sales, beside his tree", s == 403, f"status {s} {b['error']['messages'][0]['code'] if isinstance(b, dict) and b.get('error') else ''}")
    s, b, _, _ = hire(boris, company)
    failures += not check("Boris is refused in Company, above his tree", s == 403, f"status {s}")
    s, b, _, _ = hire(carla, backend)
    failures += not check("Carla, who manages nothing, is refused in Backend", s == 403, f"status {s}")
    s, b, _, _ = hire(admin, sales, "Admin")
    failures += not check("an administrator hires into Sales without being a manager of anything", s == 200, f"status {s}")
    s, b, _, _ = hire(boris, str(uuid.uuid4()))
    failures += not check("a department that does not exist gets the same refusal as one he may not manage", s == 403, f"status {s}")

    say("\n== the tree changes: Backend moves under Sales")
    s, b, _, _ = call(opener(), "PUT", f"{NGINX}/api/departments/{backend}/parent", {"parentDepartmentId": sales}, bearer(admin_plain))
    assert s == 200, ("move", s, b)
    moved_at = time.time()
    status_after, when, _ = eventually_allowed(boris, backend, 403)
    failures += not check("Boris loses Backend once the move has reached the store", status_after == 403,
                          f"refused {(when and time.time() - moved_at) and ('%.1f' % (time.time() - moved_at))} s after the move" if when else f"status {status_after}")
    s, _, _, _ = call(opener(), "PUT", f"{EMPLOYEE}/api/employees/departments/{sales}/managers/{ids['carla']}", headers=bearer(admin))
    s, b, _, _ = hire(carla, backend)
    failures += not check("Carla, manager of Sales, now hires into Backend, which is under Sales", s == 200, f"status {s}")

    say("\n== the cost of asking: a hire by a manager (one check) against a hire by an administrator (none)")
    manager = [hire(carla, backend)[3] for _ in range(30)]
    administrator = [hire(admin, backend, "Admin")[3] for _ in range(30)]
    q = lambda xs: (statistics.median(xs), sorted(xs)[int(len(xs) * 0.95) - 1])
    say(f"hire by a manager:        p50 {q(manager)[0]:.0f} ms   p95 {q(manager)[1]:.0f} ms")
    say(f"hire by an administrator: p50 {q(administrator)[0]:.0f} ms   p95 {q(administrator)[1]:.0f} ms")

    say("\n== OpenFGA stops")
    subprocess.run(["docker", "stop", "fga_drill"], capture_output=True)
    started = time.time()
    s, b, _, ms = hire(carla, backend)
    failures += not check("a manager's hire is refused as unavailable, not allowed and not denied", s == 503, f"status {s} in {ms:.0f} ms")
    s, b, _, ms = hire(admin, backend, "Admin")
    failures += not check("an administrator still hires", s == 200, f"status {s} in {ms:.0f} ms")
    s, b, _, ms = call(opener(), "GET", f"{EMPLOYEE}/api/employees?pageSize=1", headers=bearer(carla))
    failures += not check("reading employees does not depend on it", s == 200, f"status {s}")

    say("\n== OpenFGA starts again (its data is in Postgres)")
    subprocess.run(["docker", "start", "fga_drill"], capture_output=True)
    for _ in range(60):
        if subprocess.run(["curl", "-s", "-o", "/dev/null", "http://localhost:18090/healthz"]).returncode == 0:
            break
        time.sleep(1)
    s, lag, _ = eventually_allowed(carla, backend, 200, limit=40)
    failures += not check("the manager's rights are back without anything being replayed", s == 200, f"status {s}")

    say(f"\n{'ALL PASSED' if not failures else str(failures) + ' FAILED'}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
