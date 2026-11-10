# Backup container of the PAF (O11, RNF14). Same base as the compose's postgres service on purpose: pg_dump refuses
# a server of a newer major version, and pg_restore of the restore test must read what this pg_dump wrote.
# aws-cli talks S3 to the R2 backup bucket (conditional PutObject, ARQ l.388); tzdata, flock and sha256sum come with
# the base image.
FROM postgres:18-alpine

RUN apk add --no-cache aws-cli curl

COPY paf-backup.sh /usr/local/bin/paf-backup
RUN chmod 0755 /usr/local/bin/paf-backup

ENTRYPOINT ["paf-backup"]
CMD ["agendar"]
