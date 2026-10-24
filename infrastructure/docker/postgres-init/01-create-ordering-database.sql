-- Runs once, when the postgres-data volume is first initialised. The EF Core services create
-- their own databases on migrate, but Marten (ordering) only creates schema objects inside an
-- existing database. The Helm chart does the same job with an init container.
CREATE DATABASE ticketflow_ordering;
