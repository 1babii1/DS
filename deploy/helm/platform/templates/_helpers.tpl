{{/* The settings of one service: its own entry over the defaults. Call with (dict "root" $ "name" "employee"). */}}
{{- define "platform.service" -}}
{{- $svc := index .root.Values.services .name -}}
{{- toYaml (merge (deepCopy $svc) (deepCopy .root.Values.defaults)) -}}
{{- end -}}

{{/* Labels of a pod and of the workload that owns it. `track` is stable or canary. */}}
{{- define "platform.labels" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
app.kubernetes.io/part-of: platform
app.kubernetes.io/managed-by: {{ .root.Release.Service }}
helm.sh/chart: {{ .root.Chart.Name }}-{{ .root.Chart.Version }}
platform/track: {{ default "stable" .track }}
{{- end -}}

{{/* What a Deployment's selector matches: one track of one service. */}}
{{- define "platform.selector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
platform/track: {{ default "stable" .track }}
{{- end -}}

{{/* What a Service and a PodDisruptionBudget match: every pod of the service, whichever track it is on. */}}
{{- define "platform.serviceSelector" -}}
app.kubernetes.io/name: {{ .name }}
app.kubernetes.io/instance: {{ .root.Release.Name }}
{{- end -}}

{{- define "platform.image" -}}
{{- $reg := default "" .root.Values.imageRegistry -}}
{{- printf "%s%s:%s" (ternary (printf "%s/" $reg) "" (ne $reg "")) .repository .tag -}}
{{- end -}}

{{/*
One Deployment of one service on one track. Call with (dict "root" $ "name" n "s" settings "track" "stable" "replicas" r "tag" t "withReplicas" bool).
The stable track and the canary track are the same pod, differing in image tag and count; the Service sends traffic to both, in
proportion to how many pods each has.
*/}}
{{- define "platform.deployment" -}}
{{- $root := .root -}}
{{- $name := .name -}}
{{- $s := .s -}}
{{- $track := .track -}}
apiVersion: apps/v1
kind: Deployment
metadata:
  name: {{ if eq $track "canary" }}{{ $name }}-canary{{ else }}{{ $name }}{{ end }}
  labels:
    {{- include "platform.labels" (dict "root" $root "name" $name "track" $track) | nindent 4 }}
spec:
  {{- if .withReplicas }}
  replicas: {{ .replicas }}
  {{- end }}
  strategy:
    type: RollingUpdate
    rollingUpdate:
      maxUnavailable: {{ $s.strategy.maxUnavailable }}
      maxSurge: {{ $s.strategy.maxSurge }}
  selector:
    matchLabels:
      {{- include "platform.selector" (dict "root" $root "name" $name "track" $track) | nindent 6 }}
  template:
    metadata:
      labels:
        {{- include "platform.labels" (dict "root" $root "name" $name "track" $track) | nindent 8 }}
    spec:
      terminationGracePeriodSeconds: {{ $s.terminationGracePeriodSeconds }}
      # Spread replicas over nodes where there is more than one, so that a node going away does not take them all.
      topologySpreadConstraints:
        - maxSkew: 1
          topologyKey: kubernetes.io/hostname
          whenUnsatisfiable: ScheduleAnyway
          labelSelector:
            matchLabels:
              {{- include "platform.serviceSelector" (dict "root" $root "name" $name) | nindent 14 }}
      containers:
        - name: {{ $name }}
          image: {{ include "platform.image" (dict "root" $root "repository" $s.image.repository "tag" .tag) }}
          imagePullPolicy: {{ $s.image.pullPolicy }}
          ports:
            - name: http
              containerPort: {{ $s.port }}
          env:
            - name: ASPNETCORE_URLS
              value: "http://*:{{ $s.port }}"
            # Flags are a mounted ConfigMap, edited without a deploy; a mounted ConfigMap is updated by swapping a symlink.
            - name: FEATURE_FLAGS_FILE
              value: /etc/platform/flags/flags.json
            {{- range $k, $v := $s.env }}
            - name: {{ $k }}
              value: {{ $v | quote }}
            {{- end }}
          {{- with $s.envFromSecrets }}
          envFrom:
            {{- range . }}
            - secretRef:
                name: {{ . }}
            {{- end }}
          {{- end }}
          # Started: the first start can be slow (migrations ran before it, but the JIT and the first connections are not free);
          # nothing else is checked until it has passed. Live: the process answers at all, and checks no dependency (restarting
          # a service because its database is down helps no one). Ready: the dependencies it needs are reachable.
          startupProbe:
            httpGet: {path: {{ $s.probes.liveness }}, port: http}
            periodSeconds: 2
            failureThreshold: 60
          livenessProbe:
            httpGet: {path: {{ $s.probes.liveness }}, port: http}
            periodSeconds: 10
            timeoutSeconds: 3
            failureThreshold: 3
          readinessProbe:
            httpGet: {path: {{ $s.probes.readiness }}, port: http}
            periodSeconds: 5
            timeoutSeconds: 3
            failureThreshold: 2
          lifecycle:
            preStop:
              exec:
                command: ["sleep", "{{ $s.preStopSleepSeconds }}"]
          resources:
            {{- toYaml $s.resources | nindent 12 }}
          securityContext:
            {{- toYaml $s.securityContext | nindent 12 }}
          volumeMounts:
            - name: tmp
              mountPath: /tmp
            - name: flags
              mountPath: /etc/platform/flags
              readOnly: true
      volumes:
        - name: tmp
          emptyDir: {}
        - name: flags
          configMap:
            name: feature-flags
            optional: true
{{- end -}}
