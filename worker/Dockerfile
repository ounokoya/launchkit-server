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

# glibc 2.36 on both sides (bookworm builder, Debian 12 runtime).
FROM gcr.io/distroless/cc-debian12:nonroot
COPY --from=builder /src/target/release/launchkit-worker /usr/local/bin/launchkit-worker
COPY shared/i18n /app/i18n
ENV I18N_DIR=/app/i18n
USER 65532:65532 # nonroot, numeric so that Kubernetes can check runAsNonRoot
ENTRYPOINT ["/usr/local/bin/launchkit-worker"]
