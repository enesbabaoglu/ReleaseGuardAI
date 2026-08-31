FROM node:22.21.1-alpine

ENV NODE_ENV=production \
    DASHBOARD_HOST=0.0.0.0
WORKDIR /app

COPY src/ReleaseGuard.Dashboard/package.json ./
COPY src/ReleaseGuard.Dashboard/server.mjs ./
COPY src/ReleaseGuard.Dashboard/auth.mjs ./
COPY src/ReleaseGuard.Dashboard/public/ ./public/

USER node
EXPOSE 3000
CMD ["node", "server.mjs"]
