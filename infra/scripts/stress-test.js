import http from 'k6/http';
import { check } from 'k6';

export const options = {
  stages: [
    { duration: '30s', target: 100 },
    { duration: '1m', target: 300 },
    { duration: '2m', target: 500 },
    { duration: '1m', target: 300 },
    { duration: '30s', target: 0 },
  ],
  thresholds: {
    http_req_duration: ['p(95)<1000'],
  },
};

export default function () {
  const targets = [
    'https://nginx.thany.click/',
    'https://haproxy.thany.click/',
  ];

  const target = targets[Math.floor(Math.random() * targets.length)];
  const res = http.get(target);

  check(res, {
    'status is 200': (r) => r.status === 200,
  });
}
