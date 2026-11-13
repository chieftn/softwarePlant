# Software Factory Framework

This repository builds the production framework. genUI is its pathfinding product, not the framework itself. See [architecture](docs/architecture.md) and the [glossary](docs/glossary.md).

The [plant model](docs/plant-model.md) defines the selected approach: .NET, Microsoft Agent Framework, and the Copilot SDK on VM-based workbenches. The initial [workbench implementation](src/Workbench/README.md) contains a Windows host, a WSL coding worker, shared contracts, and publishing/deployment scripts.
