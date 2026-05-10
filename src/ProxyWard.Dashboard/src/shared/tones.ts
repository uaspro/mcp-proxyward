export function healthTone(status: string) {
  if (status === 'healthy') {
    return 'allow'
  }

  if (status === 'unhealthy') {
    return 'block'
  }

  return 'warn'
}
