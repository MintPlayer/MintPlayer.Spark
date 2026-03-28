FROM nginx:latest
COPY schemas/ /usr/share/nginx/html/schemas/
COPY index.html /usr/share/nginx/html/
COPY nginx.conf /etc/nginx/conf.d/default.conf
EXPOSE 80
LABEL org.opencontainers.image.source=https://github.com/MintPlayer/MintPlayer.Spark
