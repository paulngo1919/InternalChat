# Data Model: Responsive App Redesign

*Note: This feature is purely frontend styling and layout modifications. There are no changes to the backend data model, PostgreSQL schema, Redis keyspace, or application entities.*

## Frontend State Model (UI State)

While no domain data models change, the frontend will need to manage the following UI state:

### LayoutState
- `isMobile`: boolean (viewport < 768px)
- `isTablet`: boolean (viewport >= 768px and < 1024px)
- `isDesktop`: boolean (viewport >= 1024px)
- `sidebarOpen`: boolean (true by default on desktop, false by default on mobile)
- `activeView`: 'channel_list' | 'chat_view' (used for mobile navigation)
