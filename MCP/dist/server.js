import express from "express";
import { McpServer } from "@modelcontextprotocol/sdk/server/mcp.js";
import { StreamableHTTPServerTransport } from "@modelcontextprotocol/sdk/server/streamableHttp.js";
import { z } from "zod/v3";
const CALAMITY_API_BASE_URL = process.env.CALAMITY_API_BASE_URL?.replace(/\/+$/, "");
if (!CALAMITY_API_BASE_URL) {
    throw new Error("CALAMITY_API_BASE_URL environment variable is required");
}
const server = new McpServer({
    name: "mcp-streamable-http",
    version: "1.0.0",
});
// Geocode tool
server.registerTool("geocode", {
    description: "Geocode a location search string to coordinates and address details. These coordinates can be used for getting specific alerts",
    inputSchema: {
        query: z.string().describe("The location search string to geocode"),
    },
}, async ({ query }) => {
    const response = await fetch(`${CALAMITY_API_BASE_URL}/geocode?query=${encodeURIComponent(query)}`);
    const coords = await response.json();
    return {
        content: [
            {
                type: "text",
                text: `lat: ${coords[0]}, lon: ${coords[1]}`
            },
        ],
    };
});
// Alert map tool
server.registerTool("alert-map", {
    description: "Get a static alert map image URL for a given location. Use the geocode tool first to obtain latitude and longitude.",
    inputSchema: {
        lat: z.number().describe("Latitude of the location (from the geocode tool)"),
        lon: z.number().describe("Longitude of the location (from the geocode tool)"),
    },
}, async ({ lat, lon }) => {
    const url = `${CALAMITY_API_BASE_URL}/map/static?lat=${lat}&lon=${lon}&overlay=alert`;
    return {
        content: [
            {
                type: "text",
                text: url,
            },
        ],
    };
});
// Fire map tool
server.registerTool("alert-fire", {
    description: "Get a static fire map image URL for a given location. Use the geocode tool first to obtain latitude and longitude.",
    inputSchema: {
        lat: z.number().describe("Latitude of the location (from the geocode tool)"),
        lon: z.number().describe("Longitude of the location (from the geocode tool)"),
    },
}, async ({ lat, lon }) => {
    const url = `${CALAMITY_API_BASE_URL}/map/static?lat=${lat}&lon=${lon}&overlay=fire`;
    return {
        content: [
            {
                type: "text",
                text: url,
            },
        ],
    };
});
// Get active alerts tool
server.registerTool("get-alerts", {
    description: "Get active weather alerts for a given location. Use the geocode tool first to obtain latitude and longitude. Returns alerts across land, county, fire, and coastal zones.",
    inputSchema: {
        lat: z.number().describe("Latitude of the location (from the geocode tool)"),
        lon: z.number().describe("Longitude of the location (from the geocode tool)"),
    },
}, async ({ lat, lon }) => {
    const response = await fetch(`${CALAMITY_API_BASE_URL}/nws/zone?lat=${lat}&lon=${lon}`);
    const data = await response.json();
    return {
        content: [
            {
                type: "text",
                text: JSON.stringify(data, null, 2),
            },
        ],
    };
});
const app = express();
app.use(express.json());
app.post("/mcp", async (req, res) => {
    console.log("Received MCP request:", req.body);
    try {
        const transport = new StreamableHTTPServerTransport({
            sessionIdGenerator: undefined, // set to undefined for stateless servers
        });
        res.on("close", () => {
            transport.close();
        });
        await server.connect(transport);
        await transport.handleRequest(req, res, req.body);
    }
    catch (error) {
        console.error("Error handling MCP request:", error);
        if (!res.headersSent) {
            res.status(500).json({
                jsonrpc: "2.0",
                error: {
                    code: -32603,
                    message: "Internal server error",
                },
                id: null,
            });
        }
    }
});
app.get("/mcp", async (req, res) => {
    console.log("Received GET MCP request");
    res.writeHead(405).end(JSON.stringify({
        jsonrpc: "2.0",
        error: {
            code: -32000,
            message: "Method not allowed.",
        },
        id: null,
    }));
});
app.delete("/mcp", async (req, res) => {
    console.log("Received DELETE MCP request");
    res.writeHead(405).end(JSON.stringify({
        jsonrpc: "2.0",
        error: {
            code: -32000,
            message: "Method not allowed.",
        },
        id: null,
    }));
});
// Start the server
const PORT = process.env.PORT || 3000;
app.listen(PORT, () => {
    console.log(`MCP Streamable HTTP Server listening on port ${PORT}`);
});
