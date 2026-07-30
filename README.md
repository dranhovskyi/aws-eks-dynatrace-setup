# Todo API on Amazon EKS (EC2) with Dynatrace

End-to-end path: a small ASP.NET Core Todo API → Git repo → Docker image →
Amazon ECR → Amazon EKS cluster running on EC2 worker nodes → Dynatrace
full-stack observability.

```
todo-eks-dynatrace/
├── src/TodoApi/          # .NET 8 minimal API (Todo CRUD)
├── k8s/                  # Kubernetes manifests
├── eks/                  # eksctl cluster config (EKS on EC2)
├── dynatrace/             # Dynatrace DynaKube custom resource
└── README.md              # this guide
```

## Prerequisites

Install these once, locally:

| Tool | Purpose | Check |
|---|---|---|
| [.NET 8 SDK](https://dotnet.microsoft.com/download) | build/run the API | `dotnet --version` |
| [Docker](https://docs.docker.com/get-docker/) | build the container image | `docker --version` |
| [AWS CLI v2](https://docs.aws.amazon.com/cli/latest/userguide/getting-started-install.html) | talk to AWS | `aws --version` |
| [eksctl](https://eksctl.io/installation/) | create the EKS cluster | `eksctl version` |
| [kubectl](https://kubernetes.io/docs/tasks/tools/) | talk to the cluster | `kubectl version --client` |
| [Helm 3](https://helm.sh/docs/intro/install/) | install the Dynatrace Operator | `helm version` |

You also need an AWS account with admin (or near-admin) IAM permissions, and
a Dynatrace SaaS or Managed tenant (a [free trial](https://www.dynatrace.com/trial/)
works fine).

Run `aws configure` and set your access key, secret key, and default region
before starting.

---

## Part 1 — Git repo and the .NET Todo API

### 1.1 Initialize the repo

```bash
mkdir todo-eks-dynatrace && cd todo-eks-dynatrace
git init
```

This project already includes a working ASP.NET Core 8 **minimal API**
under `src/TodoApi/`:

- `Program.cs` — endpoints: `GET/POST /api/todos`, `GET/PUT/DELETE /api/todos/{id}`, plus `GET /health` for Kubernetes probes.
- `Models/TodoItem.cs`, `Models/ITodoStore.cs` — the Todo model and an in-memory store (swap for EF Core + RDS/DynamoDB later if you want persistence).
- `Dockerfile` — multi-stage build producing a small runtime image.

If you were starting from scratch instead of using this repo, the
equivalent scaffolding command is:

```bash
dotnet new webapi -minimal -n TodoApi -o src/TodoApi
```

### 1.2 Run it locally

```bash
cd src/TodoApi
dotnet restore
dotnet run
```

The API listens on `http://localhost:8080` (see `ListenAnyIP(8080)` in
`Program.cs`). In development mode, Swagger UI is at
`http://localhost:8080/swagger`.

Test it:

```bash
curl http://localhost:8080/api/todos
curl -X POST http://localhost:8080/api/todos \
  -H "Content-Type: application/json" \
  -d '{"title":"Learn EKS"}'
```

### 1.3 Commit

```bash
cd ../..
git add .
git commit -m "Initial Todo API"
```

Push to GitHub/GitLab/etc. if you want remote history:

```bash
git remote add origin <your-repo-url>
git push -u origin main
```

---

## Part 2 — Containerize with Docker

From `src/TodoApi/`:

```bash
docker build -t todo-api:local .
docker run --rm -p 8080:8080 todo-api:local
curl http://localhost:8080/api/todos
```

If that works locally, the same image will work in EKS.

---

## Part 3 — Push the image to Amazon ECR

```bash
export AWS_ACCOUNT_ID=$(aws sts get-caller-identity --query Account --output text)
export AWS_REGION=us-east-1     # match eks/cluster-config.yaml

# Create the repository (one-time)
aws ecr create-repository --repository-name todo-api --region $AWS_REGION

# Authenticate Docker to ECR
aws ecr get-login-password --region $AWS_REGION \
  | docker login --username AWS --password-stdin \
    $AWS_ACCOUNT_ID.dkr.ecr.$AWS_REGION.amazonaws.com

# Tag and push
docker tag todo-api:local $AWS_ACCOUNT_ID.dkr.ecr.$AWS_REGION.amazonaws.com/todo-api:latest
docker push $AWS_ACCOUNT_ID.dkr.ecr.$AWS_REGION.amazonaws.com/todo-api:latest
```

**AWS Console equivalent:** ECR → *Repositories* → *Create repository*
(name `todo-api`) → open it → *View push commands* gives you the exact
login/tag/push commands above.

---

## Part 4 — Create the EKS cluster on EC2 (managed node group)

`eks/cluster-config.yaml` describes a cluster with a **managed node group**
— real EC2 instances (t3.medium × 2–4), not Fargate:

```bash
eksctl create cluster -f eks/cluster-config.yaml
```

This takes 15–20 minutes: it creates a VPC, the EKS control plane, an IAM
OIDC provider, an Auto Scaling Group of EC2 instances, and installs the
`vpc-cni`, `coredns`, `kube-proxy`, `aws-ebs-csi-driver`, and
`metrics-server` add-ons.

Verify:

```bash
kubectl get nodes -o wide      # should show 2 EC2 instances, STATUS=Ready
eksctl get cluster --region $AWS_REGION
```

**AWS Console equivalent:** you *can* click through EKS → *Add cluster* →
*Create*, then EC2 → configure a launch template, then EKS → *Compute* tab
→ *Add node group* and point it at that template — but this is ~30+ manual
steps across IAM, VPC, EC2 and EKS screens. `eksctl` does the same thing
from one YAML file, which is why it's the recommended path. After creation,
open **EKS console → Clusters → todo-cluster** to see the cluster,
its EC2-backed node group (Compute tab), and networking details — everything
eksctl provisioned is visible and editable there.

---

## Part 5 — Deploy the Todo API to the cluster

Update `k8s/deployment.yaml` with your actual ECR image URI (replace the
`<AWS_ACCOUNT_ID>`/`<AWS_REGION>` placeholders), then:

```bash
kubectl apply -f k8s/namespace.yaml
kubectl apply -f k8s/deployment.yaml
kubectl apply -f k8s/service.yaml
kubectl apply -f k8s/hpa.yaml
```

Watch it come up:

```bash
kubectl get pods -n todo-app -w
kubectl get svc todo-api -n todo-app
```

The Service is `type: LoadBalancer`, so EKS's built-in AWS cloud-provider
integration automatically provisions a Network Load Balancer. Once
`EXTERNAL-IP` (a DNS hostname) appears:

```bash
curl http://<EXTERNAL-IP>/api/todos
```

**AWS Console equivalent:** EC2 console → *Load Balancers* shows the NLB
that was just created for you; EKS console → *todo-cluster* → *Resources*
tab lets you browse the `todo-app` namespace, pods, and deployment
directly in the console.

---

## Part 6 — Connect the cluster to Dynatrace

### 6.1 Get a Dynatrace API token

In your Dynatrace tenant: **Account Management → Access tokens** (or
**Settings → Integration → Dynatrace API** on older tenants) → create a
token named `k8s-operator` with these scopes:
`Read entities`, `Write entities`, `Read configuration`, `Write configuration`,
`PaaS integration - installer download`.

Note your **environment ID** and **API URL**, e.g.
`https://abc12345.live.dynatrace.com/api`.

### 6.2 Install the Dynatrace Operator

```bash
helm install dynatrace-operator oci://public.ecr.aws/dynatrace/dynatrace-operator \
  -n dynatrace --create-namespace --atomic
```

### 6.3 Create the token secret

```bash
kubectl -n dynatrace create secret generic dynakube \
  --from-literal="apiToken=<YOUR_API_TOKEN>"
```

### 6.4 Apply the DynaKube custom resource

Edit `dynatrace/dynakube.yaml` and replace `<YOUR_ENVIRONMENT_ID>` with your
real environment ID, then:

```bash
kubectl apply -f dynatrace/dynakube.yaml
```

This deploys `cloudNativeFullStack` mode: a OneAgent DaemonSet on every EC2
node (host/infra metrics) plus automatic code-module injection into pods in
namespaces labeled `inject-dynatrace: "true"` — which `k8s/namespace.yaml`
already sets on `todo-app`. If you deployed the app before applying
DynaKube, restart the pods so the injection webhook can do its work:

```bash
kubectl rollout restart deployment/todo-api -n todo-app
```

### 6.5 Verify

```bash
kubectl get pods -n dynatrace       # operator, webhook, csi-driver, oneagent pods
kubectl get dynakube -n dynatrace   # should show PHASE=Running
```

**Dynatrace UI:** go to **Kubernetes** in the left nav — `todo-cluster`
should appear within a few minutes, followed by the `todo-app` namespace,
the `todo-api` workload, and its EC2 hosts. Response-time and trace data for
the API typically shows up under **Applications & Microservices** shortly
after.

---

## Part 7 — Cleanup (avoid ongoing AWS charges)

```bash
kubectl delete -f k8s/hpa.yaml -f k8s/service.yaml -f k8s/deployment.yaml -f k8s/namespace.yaml
kubectl delete -f dynatrace/dynakube.yaml
helm uninstall dynatrace-operator -n dynatrace
eksctl delete cluster -f eks/cluster-config.yaml
aws ecr delete-repository --repository-name todo-api --region $AWS_REGION --force
```

`eksctl delete cluster` tears down the EC2 node group, control plane, and
the VPC/networking eksctl created — check the CloudFormation console
(stacks prefixed `eksctl-todo-cluster-*`) if you want to confirm nothing is
left behind.

---

## What each piece is doing, in one line each

- **`Program.cs`** — the actual Todo API: 5 REST endpoints + a health check.
- **`Dockerfile`** — builds a small, non-root runtime image from that code.
- **ECR** — your private registry; EKS nodes pull the image from here.
- **`eks/cluster-config.yaml`** — declares an EKS control plane + EC2
  managed node group (real instances, autoscaling 2→4) instead of Fargate.
- **`k8s/*.yaml`** — namespace, Deployment (2 replicas, probes hitting
  `/health`), Service (`LoadBalancer` → AWS NLB), and an optional HPA.
- **Dynatrace Operator + DynaKube** — a Kubernetes-native controller that
  rolls out OneAgent to every EC2 node and injects tracing into your pods,
  so you get infrastructure, Kubernetes, and application-level observability
  for the Todo API without touching its code.
