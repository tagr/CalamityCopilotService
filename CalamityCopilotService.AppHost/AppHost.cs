var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.CalamityCopilotService_Api>("calamitycopilotservice-api");

builder.Build().Run();
