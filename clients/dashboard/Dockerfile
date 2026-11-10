# syntax=docker/dockerfile:1.7

FROM node:22.23.4-alpine3.24 AS build
WORKDIR /app
COPY package.json package-lock.json ./
RUN npm ci
COPY . .
RUN npm run build && node scripts/check-csp-hash.mjs

FROM nginx:1.31.8-alpine3.24 AS runtime
WORKDIR /usr/share/nginx/html
RUN apk add --no-cache gettext
RUN rm -rf ./* /etc/nginx/conf.d/default.conf
COPY docker/nginx.conf /etc/nginx/conf.d/default.conf
COPY docker/security-headers.conf /etc/nginx/snippets/security-headers.conf
COPY --from=build /app/dist/ ./
COPY docker/config.json.template /etc/fsh/config.json.template
COPY docker/csp.conf.template /etc/fsh/csp.conf.template
COPY docker/docker-entrypoint.sh /docker-entrypoint.sh
RUN chmod +x /docker-entrypoint.sh
EXPOSE 80
ENTRYPOINT ["/docker-entrypoint.sh"]
