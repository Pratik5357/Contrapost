import type { NextConfig } from "next";

const config: NextConfig = {
  // A self-contained server for the container image (see Dockerfile).
  output: "standalone",
  poweredByHeader: false,
};

export default config;
