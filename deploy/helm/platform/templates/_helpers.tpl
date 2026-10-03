{{/* The settings of one service: its own entry over the defaults. Call with (dict "root" $ "name" "employee"). */}}
{{- define "platform.service" -}}
{{- $svc := index .root.Values.services .name -}}
{{- toYaml (merge (deepCopy $svc) (deepCopy .root.Values.defaults)) -}}
{{- end -}}

{{- define "platform.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/part-of: platform
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ .root.Chart.Name }}-{{ .root.Chart.Version }}
{{- end -}}

{{- define "platform.selector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end -}}

{{- define "platform.image" -}}
{{- $reg := default "" .root.Values.imageRegistry -}}
{{- printf "%s%s:%s" (ternary (printf "%s/" $reg) "" (ne $reg "")) .repository .tag -}}
{{- end -}}
