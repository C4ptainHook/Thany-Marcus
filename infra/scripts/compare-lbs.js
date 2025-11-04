import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Trend } from 'k6/metrics';

const nginxRequests = new Counter('nginx_requests');
const haproxyRequests = new Counter('haproxy_requests');
const nginxDuration = new Trend('nginx_duration');
const haproxyDuration = new Trend('haproxy_duration');

export const options = {
  stages: [
    { duration: '1m', target: 50 },
    { duration: '3m', target: 100 },
    { duration: '2m', target: 200 },
    { duration: '1m', target: 0 },
  ],
};

export default function () {
  const nginxRes = http.get('https://nginx.thany.click/');
  nginxRequests.add(1);
  nginxDuration.add(nginxRes.timings.duration);

  check(nginxRes, {
    'nginx status 200': (r) => r.status === 200,
  });

  sleep(0.5);

  const haproxyRes = http.get('https://haproxy.thany.click/');
  haproxyRequests.add(1);
  haproxyDuration.add(haproxyRes.timings.duration);

  check(haproxyRes, {
    'haproxy status 200': (r) => r.status === 200,
  });

  sleep(0.5);
}
