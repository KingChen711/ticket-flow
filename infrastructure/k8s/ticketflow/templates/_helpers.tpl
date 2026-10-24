{{/* Build an immutable application image reference. */}}
{{- define "ticketflow.appImage" -}}
{{- $root := .root -}}
{{- if or (not $root.Values.appImages.tag) (eq $root.Values.appImages.tag "latest") -}}
{{- fail "appImages.tag must be an immutable tag and cannot be 'latest'" -}}
{{- end -}}
{{- if $root.Values.appImages.registry -}}
{{ trimSuffix "/" $root.Values.appImages.registry }}/{{ .image }}:{{ $root.Values.appImages.tag }}
{{- else -}}
{{ .image }}:{{ $root.Values.appImages.tag }}
{{- end -}}
{{- end -}}

{{/* Restricted defaults shared by application and init containers. */}}
{{- define "ticketflow.restrictedContainerSecurityContext" -}}
allowPrivilegeEscalation: false
capabilities: { drop: [ALL] }
runAsNonRoot: true
runAsUser: 1654
{{- end -}}

{{/* Common application configuration. Secret-backed values never enter a ConfigMap. */}}
{{- define "ticketflow.serviceEnv" -}}
{{- $root := .root -}}
{{- $svc := .service -}}
- name: ASPNETCORE_ENVIRONMENT
  value: {{ $root.Values.runtime.environment | quote }}
- name: Keycloak__Authority
  value: {{ $root.Values.runtime.keycloak.authority | quote }}
- name: Keycloak__ServerUrl
  value: {{ $root.Values.runtime.keycloak.serverUrl | quote }}
- name: Keycloak__HealthUrl
  value: {{ $root.Values.runtime.keycloak.healthUrl | quote }}
- name: Keycloak__ValidIssuers__0
  value: {{ $root.Values.runtime.keycloak.authority | quote }}
- name: Keycloak__ValidIssuers__1
  value: {{ $root.Values.runtime.keycloak.browserIssuer | quote }}
{{- if $svc.usesServiceToken }}
- name: Keycloak__TokenEndpoint
  value: {{ $root.Values.runtime.keycloak.tokenEndpoint | quote }}
- name: Keycloak__ServiceClientId
  value: {{ $root.Values.runtime.keycloak.serviceClientId | quote }}
- name: Keycloak__ServiceClientSecret
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: service-client-secret
{{- end }}
- name: Elasticsearch__Uri
  value: {{ $root.Values.runtime.elasticsearchUri | quote }}
- name: Otlp__Endpoint
  value: {{ $root.Values.runtime.otlpEndpoint | quote }}
- name: Database__MigrateOnStartup
  value: "false"
- name: Schema__InitializeOnStartup
  value: "false"
- name: Marten__AutoCreateSchemaObjects
  value: "false"
{{- if $svc.databaseSecretKey }}
- name: ConnectionStrings__Default
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: {{ $svc.databaseSecretKey }}
{{- end }}
{{- if $svc.usesRabbit }}
- name: RabbitMq__Host
  value: {{ $root.Values.runtime.rabbitmq.host | quote }}
- name: RabbitMq__VirtualHost
  value: {{ $root.Values.runtime.rabbitmq.virtualHost | quote }}
- name: RabbitMq__UseSsl
  value: {{ $root.Values.runtime.rabbitmq.useSsl | quote }}
- name: RabbitMq__ServerName
  value: {{ $root.Values.runtime.rabbitmq.serverName | quote }}
- name: RabbitMq__Username
  value: {{ $svc.rabbitUser | quote }}
- name: RabbitMq__Password
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: {{ printf "rabbitmq-%s-password" .name }}
{{- end }}
{{- if $svc.usesKafka }}
- name: Kafka__Host
  value: {{ $root.Values.runtime.kafka.host | quote }}
- name: Kafka__UseSsl
  value: {{ $root.Values.runtime.kafka.useSsl | quote }}
- name: Kafka__UseSasl
  value: {{ $root.Values.runtime.kafka.useSasl | quote }}
- name: Kafka__SaslMechanism
  value: {{ $root.Values.runtime.kafka.saslMechanism | quote }}
{{- if $root.Values.runtime.kafka.useSasl }}
- name: Kafka__Username
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: kafka-username
- name: Kafka__Password
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: kafka-password
{{- end }}
{{- if $root.Values.runtime.kafka.caSecretName }}
- name: Kafka__CaLocation
  value: /etc/ticketflow/kafka-ca/{{ $root.Values.runtime.kafka.caSecretKey }}
{{- end }}
- name: Kafka__ReconnectBackoffMs
  value: {{ $root.Values.runtime.kafka.reconnectBackoffMs | quote }}
- name: Kafka__ReconnectBackoffMaxMs
  value: {{ $root.Values.runtime.kafka.reconnectBackoffMaxMs | quote }}
{{- end }}
{{- if $svc.usesRedis }}
- name: Redis__ConnectionString
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: redis-connection
{{- end }}
{{- if $svc.usesMongo }}
- name: Mongo__ConnectionString
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: mongo-connection
- name: Mongo__InitializeIndexesOnStartup
  value: "false"
{{- end }}
{{- if eq .name "notification" }}
- name: Mongo__Database
  value: {{ $root.Values.runtime.mongo.notificationDatabase | quote }}
{{- else if eq .name "recommendation" }}
- name: Mongo__Database
  value: {{ $root.Values.runtime.mongo.recommendationDatabase | quote }}
{{- end }}
{{- if $svc.usesClickhouse }}
- name: ClickHouse__ConnectionString
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: clickhouse-connection
{{- end }}
{{- if $svc.usesIdentityAdmin }}
- name: Keycloak__AdminClientSecret
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: keycloak-admin-client-secret
{{- end }}
{{- if $svc.usesMinio }}
- name: Minio__Endpoint
  value: {{ $root.Values.runtime.minio.endpoint | quote }}
- name: Minio__HealthUrl
  value: {{ $root.Values.runtime.minio.healthUrl | quote }}
- name: Minio__UseSsl
  value: {{ $root.Values.runtime.minio.useSsl | quote }}
- name: Minio__AccessKey
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: minio-access-key
- name: Minio__SecretKey
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: minio-secret-key
{{- end }}
{{- if $svc.usesFunctions }}
- name: Aws__LambdaServiceUrl
  value: {{ $root.Values.runtime.functions.serviceUrl | quote }}
- name: Aws__HealthUrl
  value: {{ $root.Values.runtime.functions.healthUrl | quote }}
- name: Aws__AccessKey
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: aws-access-key
- name: Aws__SecretKey
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: aws-secret-key
{{- end }}
{{- if eq .name "gateway" }}
{{- range $index, $origin := $root.Values.runtime.cors.allowedOrigins }}
- name: Cors__AllowedOrigins__{{ $index }}
  value: {{ $origin | quote }}
{{- end }}
{{- end }}
{{- if $svc.usesNotificationProvider }}
- name: Delivery__Provider
  value: {{ $root.Values.runtime.notification.provider | quote }}
- name: Delivery__BaseUrl
  value: {{ $root.Values.runtime.notification.baseUrl | quote }}
- name: Delivery__HealthUrl
  value: {{ $root.Values.runtime.notification.healthUrl | quote }}
- name: Delivery__ApiKey
  valueFrom:
    secretKeyRef:
      name: {{ $root.Values.secrets.name }}
      key: notification-api-key
{{- end }}
{{- end -}}
