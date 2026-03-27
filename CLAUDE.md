# Manuals — Project Memory

## Architecture

We follow the **onion / clean architecture** model:

- **Entries (Controllers/Hubs)** depend on **Business Logic (Services)**
- **Services** depend on **Persistence (Data Layer)**

This layering keeps the codebase clean, reusable, and testable, and eliminates any possibility of circular dependencies. Never allow a lower layer to reference a higher one.
