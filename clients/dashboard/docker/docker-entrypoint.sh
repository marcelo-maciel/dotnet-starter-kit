#!/bin/sh
set -e

: "${FSH_API_URL:?FSH_API_URL is required (e.g. https://api.example.com)}"
: "${FSH_DEFAULT_TENANT:=root}"

export FSH_API_URL FSH_DEFAULT_TENANT

envsubst < /etc/fsh/config.json.template \
       > /usr/share/nginx/html/config.json

# The CSP names the environment's API: its origin for fetch and its ws(s) form for the SignalR hubs. Only these
# two variables are substituted, so nothing else in the template is touched.
FSH_API_ORIGIN="${FSH_API_URL%/}"
FSH_API_WS_URL="$(printf '%s' "$FSH_API_ORIGIN" | sed -e 's#^https://#wss://#' -e 's#^http://#ws://#')"
FSH_API_URL="$FSH_API_ORIGIN" FSH_API_WS_URL="$FSH_API_WS_URL" \
  envsubst '${FSH_API_URL} ${FSH_API_WS_URL}' < /etc/fsh/csp.conf.template > /etc/nginx/snippets/csp.conf

exec nginx -g 'daemon off;'
