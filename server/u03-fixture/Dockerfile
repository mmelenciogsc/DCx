FROM busybox:1.37.0-musl

WORKDIR /srv
COPY --chown=65532:65532 index.html /srv/index.html

USER 65532:65532
EXPOSE 8080
ENTRYPOINT ["httpd", "-f", "-p", "8080", "-h", "/srv"]
