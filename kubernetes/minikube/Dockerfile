FROM gcr.io/k8s-minikube/kicbase:v0.0.50

ARG REGISTRY_HOST=

ADD https://ints3.clarkinc.biz/webops-files/certs/clarkinc-256.pem /usr/local/share/ca-certificates/clarkinc-256.crt
ADD https://ints3.clarkinc.biz/webops-files/certs/corp-256.pem /usr/local/share/ca-certificates/corp-256.crt

RUN update-ca-certificates \ 
 && mkdir -p /etc/docker/certs.d/${REGISTRY_HOST} \
 && cat /usr/local/share/ca-certificates/clarkinc-256.crt /usr/local/share/ca-certificates/corp-256.crt > /etc/docker/certs.d/${REGISTRY_HOST}/ca.crt \
 && chmod 644 /etc/docker/certs.d/${REGISTRY_HOST}/ca.crt

