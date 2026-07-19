{{/*
Build the full reference for one image built by this repository. Local Helm installs keep the
existing ticket-flow-<service>:latest references; CI supplies appImages.registry and a Git SHA tag
so the exact GHCR artifacts built by its matrix are what Kind deploys.
*/}}
{{- define "ticketflow.appImage" -}}
{{- $root := .root -}}
{{- if $root.Values.appImages.registry -}}
{{ trimSuffix "/" $root.Values.appImages.registry }}/{{ .image }}
{{- else -}}
{{ .image }}
{{- end -}}
{{- if $root.Values.appImages.tag -}}:{{ $root.Values.appImages.tag }}{{- end -}}
{{- end -}}
