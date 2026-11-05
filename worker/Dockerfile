# syntax=docker/dockerfile:1
# Build context: server/ (as in compose.yaml and CI):
#   docker build -f worker/Dockerfile -t launchkit-worker .
# The texts of server/shared/i18n are copied to I18N_DIR; a volume mounted
# there (compose) takes precedence.

FROM rust:1.96-slim-bookworm AS builder
WORKDIR /src
# Dependencies first, so source changes do not rebuild them.
COPY worker/Cargo.toml worker/Cargo.lock ./
RUN mkdir src && echo 'fn main() {}' > src/main.rs && touch src/lib.rs \
    && cargo build --release --locked --bin launchkit-worker \
    && rm -rf src
COPY worker/assets ./assets
COPY worker/src ./src
RUN touch src/main.rs src/lib.rs && cargo build --release --locked --bin launchkit-worker
# The only system library the binary needs beyond glibc (TLS is rustls: no OpenSSL at all).
RUN mkdir /out && cp -L /lib/$(uname -m)-linux-gnu/libgcc_s.so.1 /out/

# glibc 2.36 on both sides (bookworm builder, Debian 12 runtime). « base-nossl »: no OpenSSL in the image, as the
# worker does not use it (its CVEs would only be dead weight to patch).
FROM gcr.io/distroless/base-nossl-debian12:nonroot
COPY --from=builder /out/libgcc_s.so.1 /usr/lib/
COPY --from=builder /src/target/release/launchkit-worker /usr/local/bin/launchkit-worker
COPY shared/i18n /app/i18n
ENV I18N_DIR=/app/i18n
# nonroot, written as numbers so that Kubernetes can check runAsNonRoot.
USER 65532:65532
ENTRYPOINT ["/usr/local/bin/launchkit-worker"]
