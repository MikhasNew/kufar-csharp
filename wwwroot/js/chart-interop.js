window.ChartInterop = {
    districtChart: null,
    typeChart: null,
    roomsChart: null,

    renderStats: function (data) {
        if (!data) return;
        this.districtChart = this._renderChart('districtChart', this.districtChart, data.byDistrict, 'Объектов');
        this.typeChart = this._renderChart('typeChart', this.typeChart, data.byType, 'Объектов');
        this.roomsChart = this._renderChart('roomsChart', this.roomsChart, data.byRooms, 'Объектов');
    },

    _renderChart: function(canvasId, chartInstance, items, labelText) {
        var ctx = document.getElementById(canvasId);
        if (!ctx || !items) return chartInstance;

        if (chartInstance) {
            chartInstance.destroy();
        }

        var labels = items.map(function(i) { return i.key; });
        var values = items.map(function(i) { return i.count; });
        var prices = items.map(function(i) { return i.avgPrice; });

        return new Chart(ctx, {
            type: 'bar',
            data: {
                labels: labels,
                datasets: [{
                    label: labelText,
                    data: values,
                    backgroundColor: 'rgba(59, 130, 246, 0.6)',
                    borderColor: 'rgba(37, 99, 235, 1)',
                    borderWidth: 1,
                    borderRadius: 4
                }]
            },
            options: {
                responsive: true,
                maintainAspectRatio: false,
                plugins: {
                    legend: { display: false },
                    tooltip: {
                        callbacks: {
                            afterLabel: function(context) {
                                return 'Ср. цена: $' + prices[context.dataIndex] + '/м²';
                            }
                        }
                    }
                },
                scales: {
                    y: { beginAtZero: true }
                }
            }
        });
    }
};
