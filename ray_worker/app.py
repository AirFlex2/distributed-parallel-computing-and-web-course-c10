import asyncio
from contextlib import asynccontextmanager
from datetime import datetime, timezone
import hmac
import json
import os
import random
import socket
import sys
import time
import uuid
from typing import Optional

from fastapi import FastAPI, Header, HTTPException
import httpx
from pydantic import BaseModel
import ray


class DispatchTaskRequest(BaseModel):
    request_id: str
    student_id: str
    group_id: str
    enrollment_id: Optional[str] = None
    requested_enrollment_id: Optional[str] = None
    is_unsafe: bool = False
    slow_min_ms: int = 200
    slow_max_ms: int = 700
    max_retries: int = 3
    faults_enabled: bool = False
    faults_dir: str = "/faults"


def _log_json(event: str, **fields) -> None:
    entry = {
        "ts": datetime.now(timezone.utc).isoformat(),
        "event": event,
        "instance": socket.gethostname(),
        **fields,
    }
    print(json.dumps(entry, ensure_ascii=False), flush=True)


def _get_ray_attempt_number() -> int:
    try:
        ctx = ray.get_runtime_context()
        # In Ray >= 2.x, try reading task attempt if exposed, fallback to 1
        attempt_attr = getattr(ctx, "get_task_attempt", None)
        if callable(attempt_attr):
            return int(attempt_attr()) + 1
    except Exception:
        pass
    return 1


@ray.remote(max_retries=3, retry_exceptions=True)
def execute_dpc_enrollment_task(payload: dict) -> dict:
    """
    Виконавець задачі РПО у Ray (О3.1, О3.4).
    Виконує повільну перевірку (200–700 мс) і передає пропозицію предметного ефекту
    до окремого приймача результату (API Result Receiver), НЕ змінюючи доменний ресурс напряму у БД.
    """
    request_id = str(payload["request_id"])
    student_id = str(payload["student_id"])
    group_id = str(payload["group_id"])
    is_unsafe = bool(payload.get("is_unsafe", False))
    min_ms = max(0, min(int(payload.get("slow_min_ms", 200)), 5000))
    max_ms = max(min_ms, min(int(payload.get("slow_max_ms", 700)), 5000))
    worker_pid = os.getpid()
    host_name = socket.gethostname()
    ray_attempt = _get_ray_attempt_number()

    delay_ms = min_ms if is_unsafe else random.randint(min_ms, max_ms)
    if delay_ms > 0:
        time.sleep(delay_ms / 1000.0)

    receiver_url = os.environ.get(
        "INTERNAL_RECEIVER_URL",
        "http://app-a:8080/v1/internal/ray-effect-receiver",
    )
    auth_token = os.environ.get("API_AUTH_TOKEN", "secret-c10-token-2026")
    internal_secret = os.environ.get("INTERNAL_RECEIVER_SECRET", "c10-internal-ray-secret")

    receiver_payload = {
        "request_id": request_id,
        "student_id": student_id,
        "group_id": group_id,
        "enrollment_id": payload.get("enrollment_id"),
        "requested_enrollment_id": payload.get("requested_enrollment_id"),
        "is_unsafe": is_unsafe,
        "worker_id": f"ray:{host_name}:pid-{worker_pid}",
        "ray_attempt": ray_attempt,
    }
    headers = {
        "Authorization": f"Bearer {auth_token}",
        "X-Ray-Internal-Secret": internal_secret,
        "Content-Type": "application/json",
    }

    with httpx.Client(timeout=20.0) as client:
        resp = client.post(receiver_url, json=receiver_payload, headers=headers)
        resp.raise_for_status()
        decision = resp.json()

    # РПО ЛР3 (О3.4): Керована відмова після запису ефекту приймачем (HTTP 200), але ДО ack завершення задачі в Ray.
    faults_enabled = bool(payload.get("faults_enabled", False))
    if faults_enabled:
        fault_dir = str(payload.get("faults_dir") or "/faults")
        flag_path = os.path.join(fault_dir, "after_effect_before_ack")
        if os.path.exists(flag_path):
            claimed_path = os.path.join(
                fault_dir, f".claimed_after_effect_before_ack_{worker_pid}_{uuid.uuid4().hex}"
            )
            try:
                os.replace(flag_path, claimed_path)
                try:
                    os.remove(claimed_path)
                except OSError:
                    pass
                _log_json(
                    "fault.hit",
                    fault="after_effect_before_ack",
                    request_id=request_id,
                    student_id=student_id,
                    group_id=group_id,
                    worker_pid=worker_pid,
                    ray_attempt=ray_attempt,
                    already_applied=decision.get("already_applied", False),
                )
                sys.stdout.flush()
                sys.stderr.flush()
                # Невелика пауза дозволяє буферу виводу Ray-воркера скинутися в stdout драйвера перед SIGKILL/abort
                time.sleep(0.05)
                os._exit(137)
            except FileNotFoundError:
                pass
            except OSError:
                pass

    return {
        "status": decision["status"],
        "rejection_reason": decision.get("rejection_reason"),
        "assigned_group_id": decision.get("assigned_group_id"),
        "roster_recorded": bool(decision.get("roster_recorded", False)),
        "already_applied": bool(decision.get("already_applied", False)),
        "delivery_count": int(decision.get("delivery_count", 1)),
        "ray_worker_pid": worker_pid,
        "ray_host": host_name,
        "ray_attempt": ray_attempt,
    }


@asynccontextmanager
async def lifespan(app: FastAPI):
    ray_address = os.environ.get("RAY_ADDRESS", "auto")
    if not ray.is_initialized():
        ray.init(address=ray_address, ignore_reinit_error=True, log_to_driver=True)
    _log_json("ray.dispatcher.started", ray_address=ray_address, cluster_resources=ray.cluster_resources())
    yield
    if ray.is_initialized():
        ray.shutdown()


app = FastAPI(title="C10 DPC Ray Task Dispatcher", lifespan=lifespan)


@app.get("/health")
async def health():
    if not ray.is_initialized():
        raise HTTPException(status_code=503, detail="Ray not initialized")
    resources = ray.cluster_resources()
    nodes = [n for n in ray.nodes() if n.get("Alive")]
    return {
        "status": "ok",
        "ray_connected": True,
        "alive_nodes": len(nodes),
        "cluster_resources": resources,
    }


@app.post("/dispatch")
async def dispatch(
    req: DispatchTaskRequest,
    authorization: Optional[str] = Header(default=None, alias="Authorization"),
    x_ray_internal_secret: Optional[str] = Header(default=None, alias="X-Ray-Internal-Secret"),
):
    expected_token = os.environ.get("API_AUTH_TOKEN", "secret-c10-token-2026")
    provided_token = ""
    if authorization and authorization.startswith("Bearer "):
        provided_token = authorization[len("Bearer "):].strip()
    if not provided_token or not hmac.compare_digest(provided_token, expected_token):
        raise HTTPException(status_code=401, detail="Unauthorized: valid service Bearer token required")

    expected_secret = os.environ.get("INTERNAL_RECEIVER_SECRET", "c10-internal-ray-secret")
    if expected_secret:
        if not x_ray_internal_secret or not hmac.compare_digest(x_ray_internal_secret, expected_secret):
            raise HTTPException(status_code=403, detail="Forbidden: invalid X-Ray-Internal-Secret")

    if not ray.is_initialized():
        raise HTTPException(status_code=503, detail="Ray cluster is not ready")

    payload = req.model_dump()
    payload["slow_min_ms"] = max(0, min(int(req.slow_min_ms), 5000))
    payload["slow_max_ms"] = max(payload["slow_min_ms"], min(int(req.slow_max_ms), 5000))
    retries = max(0, min(int(req.max_retries), 5))
    obj_ref = execute_dpc_enrollment_task.options(max_retries=retries).remote(payload)
    try:
        result = await asyncio.to_thread(ray.get, obj_ref)
        return result
    except Exception as exc:
        _log_json("ray.task.failed", request_id=req.request_id, error=str(exc))
        raise HTTPException(status_code=502, detail=f"Ray task execution failed: {exc}")
