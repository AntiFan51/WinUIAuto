// A desktop automation service is intentionally restricted to IPv4 loopback.
// This is a protocol default, not a developer workstation address.
function readServerConfig(env = process.env) {
  const value = env.PORT || "4173";
  if (!/^\d+$/.test(value)) throw new Error("PORT must be an integer between 0 and 65535");
  const port = Number(value);
  if (!Number.isSafeInteger(port) || port > 65535) {
    throw new Error("PORT must be an integer between 0 and 65535");
  }
  // Port 0 requests an OS-assigned port, used by isolated integration tests.
  return { host: "127.0.0.1", port };
}

function serviceUrl(port) {
  return `http://localhost:${port}`;
}

module.exports = { readServerConfig, serviceUrl };
