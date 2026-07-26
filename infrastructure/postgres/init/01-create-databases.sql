SELECT format('CREATE DATABASE %I OWNER %I', database_name, current_user)
FROM (VALUES
    ('ticketflow_catalog'),
    ('ticketflow_identity'),
    ('ticketflow_reservation'),
    ('ticketflow_ordering'),
    ('ticketflow_payment'),
    ('ticketflow_ticketing'),
    ('ticketflow_keycloak')
) AS databases(database_name)
WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname = database_name)
\gexec
