# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

A reference setup that takes a .NET 8 minimal-API Todo service through Docker → Amazon ECR → Amazon EKS (EC2 managed node group, not Fargate) → Dynatrace full-stack observability. `README.md` is the step-by-step runbook (Parts 1–7, including teardown); keep it in sync when changing infra files, since it quotes names, ports and commands from them.

There is no test project, linter config, or CI pipeline.

## Commands

App (run from `src/TodoApi/`):
```bash
dotnet run                                   # http://localhost:8080, Swagger at /swagger
docker build -t todo-api:local .
docker run --rm -p 8080:8080 todo-api:local
```

Infra (run from repo root):
```bash
eksctl create cluster -f eks/cluster-config.yaml     # ~15–20 min
kubectl apply -f k8s/namespace.yaml -f k8s/deployment.yaml -f k8s/service.yaml -f k8s/hpa.yaml
helm install dynatrace-operator oci://public.ecr.aws/dynatrace/dynatrace-operator -n dynatrace --create-namespace --atomic
kubectl -n dynatrace create secret generic dynakube --from-literal="apiToken=<TOKEN>"
kubectl apply -f dynatrace/dynakube.yaml
kubectl rollout restart deployment/todo-api -n todo-app   # needed if the app was deployed before DynaKube
eksctl delete cluster -f eks/cluster-config.yaml          # teardown; see README Part 7 for full order
```

## Cross-file contracts

These values are coupled across files; changing one side silently breaks the other:

- **Port 8080**: `Program.cs` (`ListenAnyIP(8080)`), `Dockerfile` (`ASPNETCORE_URLS`, `EXPOSE`), `k8s/deployment.yaml` (`containerPort`, probe ports), `k8s/service.yaml` (`targetPort`; Service exposes 80).
- **`/health` endpoint**: defined in `Program.cs`, targeted by readiness/liveness probes in `k8s/deployment.yaml`.
- **`inject-dynatrace: "true"` label**: set on the `todo-app` namespace in `k8s/namespace.yaml`, matched by `oneAgent.cloudNativeFullStack.namespaceSelector` in `dynatrace/dynakube.yaml`. This is what makes the operator inject OneAgent code modules into app pods.
- **`app: todo-api` label**: Deployment selector/template and Service selector must match.
- **HPA ↔ metrics-server**: `k8s/hpa.yaml` depends on the `metrics-server` add-on declared in `eks/cluster-config.yaml`.
- **Region `us-east-1`**: in `eks/cluster-config.yaml`, the ECR image URI in `k8s/deployment.yaml`, and README's `AWS_REGION`.
- **Dynatrace token**: never in the repo; lives in the `dynakube` secret in the `dynatrace` namespace, which the DynaKube named `dynakube` reads by default.

## App notes

- `InMemoryTodoStore` is a DI singleton backed by a `ConcurrentDictionary`, so state is per-pod and lost on restart. With 2+ replicas behind the NLB, reads can disagree between requests. Persistence (EF Core + RDS/DynamoDB) is mentioned as a future swap behind `ITodoStore`.
- Swagger is enabled unconditionally in `Program.cs` (not gated on Development).
- The runtime image runs as non-root `appuser`.

## Environment-specific values

`k8s/deployment.yaml` (ECR image URI, currently under `prod/todo-api`) and `dynatrace/dynakube.yaml` (`apiUrl` tenant) contain real account/tenant values rather than the `<AWS_ACCOUNT_ID>` / `<YOUR_ENVIRONMENT_ID>` placeholders the README describes. The README's ECR commands use repository name `todo-api`, which does not match the `prod/todo-api` path in the deployment.
