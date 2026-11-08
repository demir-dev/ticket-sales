FROM caddy:2-alpine
COPY static.Caddyfile /etc/caddy/Caddyfile
COPY src/wwwroot/index.html src/wwwroot/style.css /srv/
EXPOSE 8080
