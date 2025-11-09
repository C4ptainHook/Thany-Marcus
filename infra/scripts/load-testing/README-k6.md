# k6 Load Testing Scripts

## Test Scripts

### 1. load-test-nginx.js
Tests nginx ingress controller performance with gradual load increase.

**Usage**:
```powershell
k6 run D:\Marcus\infra\scripts\load-test-nginx.js
```

**Load profile**:
- 0-30s: Ramp up to 50 users
- 30s-2m30s: Ramp up to 100 users
- 2m30s-3m: Spike to 200 users
- 3m-4m: Hold at 200 users
- 4m-4m30s: Ramp down to 0

### 2. load-test-haproxy.js
Tests haproxy ingress controller performance with same load profile.

**Usage**:
```powershell
k6 run D:\Marcus\infra\scripts\load-test-haproxy.js
```

### 3. compare-lbs.js
Compares nginx vs haproxy side-by-side with custom metrics.

**Usage**:
```powershell
k6 run D:\Marcus\infra\scripts\compare-lbs.js
```

**Load profile**:
- 0-1m: Ramp to 50 users
- 1m-4m: Ramp to 100 users
- 4m-6m: Spike to 200 users
- 6m-7m: Ramp down to 0

**Custom metrics**:
- `nginx_requests` - Total requests to nginx
- `haproxy_requests` - Total requests to haproxy
- `nginx_duration` - Response time trend for nginx
- `haproxy_duration` - Response time trend for haproxy

### 4. stress-test.js
Aggressive stress test to trigger HPA scaling (1→10 replicas).

**Usage**:
```powershell
k6 run D:\Marcus\infra\scripts\stress-test.js
```

**Load profile**:
- 0-30s: Ramp to 100 users
- 30s-1m30s: Ramp to 300 users
- 1m30s-3m30s: Hold at 500 users (maximum load)
- 3m30s-4m30s: Ramp down to 300 users
- 4m30s-5m: Ramp down to 0

## Monitoring During Tests

### Watch HPA Scaling
```powershell
kubectl get hpa -n backend -w
```

### Watch Pod Scaling
```powershell
kubectl get pods -n backend -w
```

### Watch Pod Metrics
```powershell
kubectl top pods -n backend
```

### Grafana Dashboards
Open https://grafana.thany.click and view:
- Kubernetes / Compute Resources / Namespace (Pods) - Select "backend" namespace
- Kubernetes / Compute Resources / Workload
- Node Exporter / Nodes

## Conference Demo Workflow

**Step 1**: Show initial state
```powershell
kubectl get pods -n backend
kubectl get hpa -n backend
```

**Step 2**: Start load test
```powershell
k6 run D:\Marcus\infra\scripts\stress-test.js
```

**Step 3**: Monitor scaling in separate terminal
```powershell
kubectl get hpa -n backend -w
```

**Step 4**: Show Grafana dashboard
- Open https://grafana.thany.click
- Navigate to Kubernetes / Compute Resources / Namespace (Pods)
- Filter by "backend" namespace
- Watch CPU usage and pod count increase

**Step 5**: Compare load balancers
```powershell
k6 run D:\Marcus\infra\scripts\compare-lbs.js
```

## Output Options

### Save results to file
```powershell
k6 run --out json=results.json D:\Marcus\infra\scripts\load-test-nginx.js
```

### Generate HTML report (requires k6-reporter)
```powershell
k6 run --out json=results.json D:\Marcus\infra\scripts\load-test-nginx.js
```

## Expected Results

### HPA Scaling Trigger
- n8n-editor/n8n-worker configured with CPU limits
- HPA triggers at 50% CPU utilization
- Should see scaling start within 30-60 seconds of load
- Full scale to 10 replicas within 2-3 minutes under heavy load

### Performance Expectations
- nginx ingress: p95 < 500ms
- haproxy ingress: p95 < 500ms
- Success rate: > 90%
- Rate limiting: 30 requests/sec per IP (will see 429 errors during stress test)
