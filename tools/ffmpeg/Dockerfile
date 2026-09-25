FROM debian:bookworm-slim@sha256:3783cc01769c7b2b1b83a5c5ad96c815348e28ed7da68e2e3687004faa906251
RUN printf 'deb [check-valid-until=no] http://snapshot.debian.org/archive/debian/20260921T000000Z bookworm main\n' > /etc/apt/sources.list && rm -f /etc/apt/sources.list.d/debian.sources && apt-get update && DEBIAN_FRONTEND=noninteractive apt-get install -y --no-install-recommends gcc g++ mingw-w64 gcc-mingw-w64-x86-64 g++-mingw-w64-x86-64 binutils-mingw-w64-x86-64 make pkg-config ca-certificates xz-utils && rm -rf /var/lib/apt/lists/*
WORKDIR /work
