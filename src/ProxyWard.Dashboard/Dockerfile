# syntax=docker/dockerfile:1

FROM node:22-alpine AS build
WORKDIR /app

COPY package*.json ./
RUN npm ci

COPY . .
ARG VITE_PROXYWARD_API_BASE_URL=/
ARG VITE_PROXYWARD_ADMIN_TOKEN=
ENV VITE_PROXYWARD_API_BASE_URL=${VITE_PROXYWARD_API_BASE_URL}
ENV VITE_PROXYWARD_ADMIN_TOKEN=${VITE_PROXYWARD_ADMIN_TOKEN}
RUN npm run build

FROM nginx:alpine AS runtime

COPY nginx.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist /usr/share/nginx/html

EXPOSE 80
