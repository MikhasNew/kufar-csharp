window.MapInterop = {
    map: null,
    clusterGroup: null,
    polygonsLayer: null,
    poiLayer: null,

    init: function (elementId) {
        if (this.map) { this.map.remove(); this.map = null; }

        // Canvas renderer — рисует маркеры на canvas вместо DOM-элементов
        this.map = L.map(elementId, {
            preferCanvas: true
        }).setView([53.9006, 27.5590], 12);

        L.tileLayer('https://{s}.tile.openstreetmap.org/{z}/{x}/{y}.png', {
            maxZoom: 19, attribution: '© OpenStreetMap'
        }).addTo(this.map);

        // MarkerCluster — группирует близкие маркеры, резко снижает число DOM-элементов
        this.clusterGroup = L.markerClusterGroup({
            maxClusterRadius: 50,
            disableClusteringAtZoom: 17,
            spiderfyOnMaxZoom: true,
            chunkedLoading: true,       // Загрузка маркеров порциями — не блокирует UI
            chunkInterval: 100,
            chunkDelay: 10
        });
        this.map.addLayer(this.clusterGroup);

        this.polygonsLayer = L.layerGroup().addTo(this.map);
        this.poiLayer = L.layerGroup().addTo(this.map);

        L.control.layers(null, {
            "Объявления": this.clusterGroup,
            "Районы": this.polygonsLayer,
            "POI": this.poiLayer
        }, { collapsed: false }).addTo(this.map);
    },

    addMarkers: function (listings) {
        if (!this.clusterGroup) return;
        this.clusterGroup.clearLayers();

        var colors = { 'Buy': '#22c55e', 'Hold': '#f59e0b', 'Avoid': '#ef4444' };
        var markers = [];

        for (var i = 0; i < listings.length; i++) {
            var l = listings[i];
            if (!l.lat || !l.lon) continue;

            var color = colors[l.recommendation] || '#6b7280';

            // circleMarker рисуется на Canvas — в сотни раз легче чем divIcon
            var marker = L.circleMarker([l.lat, l.lon], {
                radius: 6,
                fillColor: color,
                color: '#fff',
                weight: 1.5,
                fillOpacity: 0.85
            });

            // Popup создаётся лениво — только при клике, а не для всех маркеров сразу
            (function(listing, c) {
                marker.on('click', function() {
                    if (!this.getPopup()) {
                        var popup = '<div style="min-width:200px">' +
                            '<b>' + (listing.title || '').substring(0, 60) + '</b><br>' +
                            '<b>$' + (listing.price || 0).toLocaleString() + '</b> · ' + (listing.pricePerSqm || 0) + ' $/м²<br>' +
                            (listing.rooms || '?') + ' комн. · ' + (listing.area || '?') + ' м²<br>' +
                            'Район: ' + (listing.district || '?') + '<br>' +
                            'Скор: <b>' + (listing.score || 0) + '</b> — <span style="color:' + c + ';font-weight:bold">' + (listing.recommendation || '?') + '</span><br>' +
                            (listing.url ? '<a href="' + listing.url + '" target="_blank">Kufar ↗</a>' : '') + '</div>';
                        this.bindPopup(popup).openPopup();
                    }
                });
            })(l, color);

            markers.push(marker);
        }

        this.clusterGroup.addLayers(markers); // Batch-добавление — одна операция вместо тысяч
    },

    addPolygons: function (polygons) {
        if (!this.polygonsLayer) return;
        this.polygonsLayer.clearLayers();
        var dc = {
            'Центральный':'#ef4444','Советский':'#f97316','Первомайский':'#eab308',
            'Партизанский':'#22c55e','Заводской':'#14b8a6','Ленинский':'#3b82f6',
            'Октябрьский':'#8b5cf6','Московский':'#ec4899','Фрунзенский':'#6366f1'
        };
        for (var name in polygons) {
            var coords = polygons[name].map(function(p){ return [p[0], p[1]]; });
            var c = dc[name] || '#6b7280';
            L.polygon(coords, { color:c, weight:2, fillOpacity:0.08, fillColor:c })
                .bindTooltip(name, { permanent:false, direction:'center' })
                .addTo(this.polygonsLayer);
        }
    },

    addPoi: function (poi) {
        if (!this.poiLayer) return;
        this.poiLayer.clearLayers();

        var poiColors = { metro:'#e11d48', park:'#22c55e', water:'#3b82f6', forest:'#15803d' };
        var poiRadius = { metro:7, park:5, water:5, forest:4 };
        var poiLabels = { metro:'M', park:'P', water:'W', forest:'F' };

        for (var i = 0; i < poi.length; i++) {
            var p = poi[i];
            var color = poiColors[p.type] || '#6b7280';
            var r = poiRadius[p.type] || 5;

            // Canvas circleMarker — легковесный
            var marker = L.circleMarker([p.lat, p.lon], {
                radius: r,
                fillColor: color,
                color: '#fff',
                weight: 2,
                fillOpacity: 0.9
            });
            marker.bindTooltip(p.name, { direction:'top', offset:[0, -8] });
            marker.addTo(this.poiLayer);

            // Радиус пешей доступности только для метро
            if (p.type === 'metro') {
                L.circle([p.lat, p.lon], {
                    radius: 800, color: color,
                    weight: 1, fillColor: color, fillOpacity: 0.04
                }).addTo(this.poiLayer);
            }
        }
    },

    invalidateSize: function () {
        if (this.map) setTimeout(function(){ this.map.invalidateSize(); }.bind(this), 100);
    }
};
