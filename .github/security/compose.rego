# METADATA
# title: Опасные привилегии сервисов Compose
# description: Запрещает привилегированный режим, пространства имён хоста и Docker socket.
# scope: package
# custom:
#   id: COMPOSE-001
#   severity: HIGH
#   input:
#     selector:
#       - type: yaml
package user.compose.security

import rego.v1

deny contains res if {
    not is_object(object.get(input, "services", null))
    res := result.new("Compose должен содержать объект services", input)
}

deny contains res if {
    is_object(input.services)
    count(input.services) == 0
    res := result.new("В Compose отсутствуют сервисы", input)
}

deny contains res if {
    some name, service in input.services
    service.privileged == true
    res := result.new(sprintf("Сервис %s использует privileged", [name]), service)
}

deny contains res if {
    some name, service in input.services
    some key in {"network_mode", "pid", "ipc"}
    object.get(service, key, "") == "host"
    res := result.new(sprintf("Сервис %s использует %s хоста", [name, key]), service)
}

deny contains res if {
    some name, service in input.services
    some volume in object.get(service, "volumes", [])
    is_string(volume)
    contains(volume, "/var/run/docker.sock")
    res := result.new(sprintf("Сервис %s монтирует Docker socket", [name]), service)
}

deny contains res if {
    some name, service in input.services
    some volume in object.get(service, "volumes", [])
    is_object(volume)
    object.get(volume, "source", "") == "/var/run/docker.sock"
    res := result.new(sprintf("Сервис %s монтирует Docker socket", [name]), service)
}
