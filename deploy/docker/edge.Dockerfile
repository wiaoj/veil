# Veil Edge (Rust) — multi-stage build.
# Build context: the `veil` repo root (this file lives at deploy/docker/).
#
#   podman build -f deploy/docker/edge.Dockerfile -t veil-edge:latest .

FROM rust:1-slim AS build
WORKDIR /src

# Cache dependency compilation: build against a stub main first, then the
# real sources, so a source-only change does not re-fetch/-build crates.
COPY edge/Cargo.toml edge/Cargo.lock* ./edge/
RUN mkdir -p edge/src && echo "fn main() {}" > edge/src/main.rs \
    && (cd edge && cargo build --release --bin veil-edge 2>/dev/null || true)

COPY edge/ ./edge/
RUN cd edge && touch src/main.rs && cargo build --release --bin veil-edge

FROM debian:stable-slim
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates \
    && rm -rf /var/lib/apt/lists/* \
    && useradd -r -u 10001 veil
COPY --from=build /src/edge/target/release/veil-edge /usr/local/bin/veil-edge
USER veil
# HTTP (8080) and HTTPS (8443) listeners; override with VEIL_LISTEN_* env.
EXPOSE 8080 8443
ENTRYPOINT ["veil-edge"]
